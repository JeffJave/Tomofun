﻿using LumTomofunCustomization.DAC;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LumTomofunCustomization.Graph
{
    public class LUMMRPProcess : PXGraph<LUMMRPProcess>
    {
        public PXCancel<MRPFilter> Cancel;

        public PXFilter<MRPFilter> Filter;
        [PXFilterable]
        public PXFilteredProcessing<LUMMRPProcessResult, MRPFilter> Transaction;

        public LUMMRPProcess()
        {
            var filter = this.Filter.Current;
            Transaction.SetProcessVisible(false);
            Transaction.SetProcessAllCaption("Process MRP");
            Transaction.SetProcessDelegate(
                delegate (List<LUMMRPProcessResult> list)
                {
                    GoProcessing(filter);
                });
            if (this.Transaction.Select().Count == 0)
                InitialData();
        }

        public static void GoProcessing(MRPFilter filter)
        {
            var graph = CreateInstance<LUMMRPProcess>();
            graph.LumMRPProcess(filter);
        }

        public virtual void LumMRPProcess(MRPFilter filter)
        {
            try
            {
                PXLongOperation.StartOperation(this, delegate ()
                {
                    // Valid ItemClass
                    if (filter.ItemClassID == null)
                    {
                        PXProcessing.SetError<LUMMRPProcessResult>("Item classID can not be empty!!");
                        throw new Exception();
                    }
                    // 全部StockItem
                    var allInventoryItem = SelectFrom<InventoryItem>
                                           .Where<InventoryItem.itemClassID.IsEqual<P.AsInt>
                                             .And<InventoryItem.itemStatus.IsEqual<P.AsString>>>
                                           .View.Select(this, filter.ItemClassID, "AC").RowCast<InventoryItem>()
                                           .Where(x => filter.Sku == null || x.InventoryID == filter?.Sku).ToList();
                    // 全部Warehouse
                    var allWarehouseData = SelectFrom<INSite>
                                           .Where<INSite.active.IsEqual<True>>
                                           .View.Select(this).RowCast<INSite>()
                                           .Where(x => filter.Warehouse == null || x.SiteID == filter?.Warehouse).ToList();
                    // 刪除預設資料
                    DeleteData(GetFixInventoryItem(),GetFixSiteID());

                    foreach (var actSku in allInventoryItem)
                    {
                        foreach (var actWarehouse in allWarehouseData)
                        {
                            var startDate = filter.Date;             // MRP 起始日期
                            var actDate = startDate;                 // MRP 執行日
                            var lastDayRemainForecase = 0;           // MRP 前一天剩餘 Forecase
                            var lastDayStock = 0;                    // MRP 前一天剩餘的 Stock
                            var Sku = actSku.InventoryID;            // MRP Sku
                            var Warehouse = actWarehouse.SiteID;     // MRP Warehouse
                            var lastDate = actDate;                  // MRP 計算最後一天
                            LUMForecaseUpload lastForecastData;      // 離計算日最新的Forecast資料

                            DeleteData(Sku, Warehouse);

                            // Forecase upload data
                            var forecaseData = SelectFrom<LUMForecaseUpload>
                                               .Where<LUMForecaseUpload.sku.IsEqual<@P.AsInt>
                                                 .And<LUMForecaseUpload.warehouse.IsEqual<@P.AsInt>>
                                                 .And<LUMForecaseUpload.country.IsEqual<@P.AsString>>>
                                               .View.Select(this, Sku, Warehouse, "US").RowCast<LUMForecaseUpload>().OrderBy(x => x.Date);
                            lastForecastData = forecaseData.OrderBy(x => x.Date).LastOrDefault(x => x.Date.Value.Date < actDate.Value.Date && x.Mrptype == "Forecast");

                            // MRPPreference
                            var mrpPreference = SelectFrom<LUMMRPPreference>.View.Select(this).RowCast<LUMMRPPreference>();

                            #region Storage Summary(當下庫存)
                            var storageGraph = PXGraph.CreateInstance<StoragePlaceEnq>();
                            storageGraph.Filter.Cache.SetValueExt<StoragePlaceEnq.StoragePlaceFilter.siteID>(storageGraph.Filter.Current, actWarehouse.SiteID);
                            storageGraph.Filter.Cache.SetValueExt<StoragePlaceEnq.StoragePlaceFilter.inventoryID>(storageGraph.Filter.Current, actSku.InventoryID);
                            var storageSummary = storageGraph.storages.Select().RowCast<StoragePlaceStatus>().ToList();
                            #endregion

                            #region Inventory Allocation Details
                            var invGraph = PXGraph.CreateInstance<InventoryAllocDetEnq>();
                            invGraph.Filter.Cache.SetValueExt<InventoryAllocDetEnqFilter.inventoryID>(invGraph.Filter.Current, actSku.InventoryID);
                            invGraph.Filter.Cache.SetValueExt<InventoryAllocDetEnqFilter.siteID>(invGraph.Filter.Current, actWarehouse.SiteID);
                            var invAllocDetails = invGraph.ResultRecords.Select().RowCast<InventoryAllocDetEnqResult>().ToList();
                            // 如果沒有Inventory Allocation 則不執行
                            lastDate = invAllocDetails.Count > 0 ? invAllocDetails.Max(x => x.PlanDate).Value.Date : startDate.Value.Date.AddDays(-1);
                            #endregion

                            #region Act Issue (Release INTran)

                            var inTransData = lastForecastData == null ? null :
                                              SelectFrom<INTran>
                                              .Where<INTran.sOShipmentType.IsEqual<P.AsString>
                                                .And<INTran.releasedDateTime.IsBetween<P.AsDateTime, P.AsDateTime>>
                                                .And<INTran.released.IsEqual<True>>>
                                              .View.Select(this, "I", lastForecastData.Date.Value.Date, actDate).RowCast<INTran>().ToList();

                            #endregion

                            #region Get Inventory calculate rules
                            var invRules = new List<string>();
                            var invInfo = SelectFrom<InventoryItem>
                                          .InnerJoin<INItemClass>.On<InventoryItem.itemClassID.IsEqual<INItemClass.itemClassID>>
                                          .InnerJoin<INAvailabilityScheme>.On<INItemClass.availabilitySchemeID.IsEqual<INAvailabilityScheme.availabilitySchemeID>>
                                          .Where<InventoryItem.inventoryID.IsEqual<P.AsInt>>
                                          .View.Select(this, actSku.InventoryID);
                            var invSchema = invInfo.RowCast<INAvailabilityScheme>().FirstOrDefault();
                            foreach (var item in invSchema.GetType().GetProperties())
                            {
                                if (item.Name.Contains("InclQty") && item.PropertyType == typeof(bool?) && (bool?)item.GetValue(invSchema) == true)
                                    invRules.Add(item.Name);
                            }
                            #endregion

                            // initial 
                            while (actDate.Value.Date <= lastDate.Value.Date)
                            {
                                var result = this.Transaction.Insert((LUMMRPProcessResult)this.Transaction.Cache.CreateInstance());

                                // 計算第一天
                                if (startDate.Value.Date == actDate.Value.Date)
                                {
                                    #region Last Stock Initial

                                    lastDayStock = (int?)storageSummary.FirstOrDefault()?.Qty ?? 0;

                                    #endregion

                                    #region Open SO(Date-1)

                                    result.PastOpenSo = (int?)
                                                        (from inv in invAllocDetails
                                                         join preference in mrpPreference
                                                         on new { A = inv.AllocationType, B = "OpenSO" } equals new { A = preference.AllocationType, B = preference.Mrptype }
                                                         where inv.PlanDate.Value.Date < actDate.Value.Date &&
                                                               invRules.IndexOf(preference.PlanType) > 0
                                                         select inv.PlanQty).Sum(x => x.Value) ?? 0;
                                    #endregion
                                }

                                #region Open SO

                                result.OpenSo = (int?)
                                                (from inv in invAllocDetails
                                                 join preference in mrpPreference
                                                 on new { A = inv.AllocationType, B = "OpenSO" } equals new { A = preference.AllocationType, B = preference.Mrptype }
                                                 where inv.PlanDate.Value.Date == actDate.Value.Date &&
                                                       invRules.IndexOf(preference.PlanType) > 0
                                                 select inv.PlanQty).Sum(x => x.Value) ?? 0;

                                #endregion

                                #region Open So Adj

                                var openSOAdj = result.OpenSo + (result.PastOpenSo ?? 0);

                                #endregion

                                // 如果ActDate有上傳Forecase
                                var actDayExistsForecase = forecaseData.FirstOrDefault(x => x.Date.Value.Date == actDate.Value.Date && x.Mrptype == "Forecast") != null;
                                // 計算第一天或 當天有上傳Forcase Forecase Base & Last Stock initial
                                if (startDate.Value.Date == actDate.Value.Date || actDayExistsForecase)
                                {
                                    #region 計算 Forecase & Forecase Base
                                    result.Forecase = (int?)forecaseData.FirstOrDefault(x => x.Date.Value.Date == actDate.Value.Date && x.Mrptype == "Forecast")?.Qty;
                                    // 如果當天有forecase 就用當天上傳資料; 
                                    if (actDayExistsForecase)
                                        result.ForecaseBase = (int?)forecaseData.FirstOrDefault(x => x.Date.Value.Date == actDate.Value.Date && x.Mrptype == "Forecast")?.Qty;
                                    // 過往無任何forecase則取0; 
                                    else if (forecaseData.FirstOrDefault(x => x.Date.Value.Date < actDate.Value.Date && x.Mrptype == "Forecast") == null)
                                        result.ForecaseBase = 0;
                                    // 找最近的上傳forecase資料並扣除ActIssues
                                    else
                                    {
                                        // 最新一筆的forecase 資料
                                        result.ForecaseBase = (int?)lastForecastData?.Qty - (int)inTransData?.Sum(x => x.Qty ?? 0);
                                        result.ForecaseBase = result.ForecaseBase < 0 ? 0 : result.ForecaseBase;
                                    }
                                    #endregion
                                }

                                #region Foreacse Initial

                                result.ForecastIntial = result.ForecaseBase == null ? lastDayRemainForecase : result.ForecaseBase;

                                #endregion

                                #region Forecase Remains + LastDay Forecase Remains

                                result.ForecaseRemains = Math.Max(result.ForecastIntial.Value - openSOAdj.Value, 0);
                                lastDayRemainForecase = result.ForecaseRemains ?? 0;

                                #endregion

                                #region Forecase Comsumption

                                result.ForecastComsumption = result?.ForecastIntial - result?.ForecaseRemains;

                                #endregion

                                #region Demand Adj

                                result.DemandAdj = Math.Max(openSOAdj.Value - result.ForecastComsumption.Value, 0);

                                #endregion

                                #region Net Demand

                                result.NetDemand = result.ForecaseBase == null ? (result.Forecase ?? 0) + result.DemandAdj : Math.Max(openSOAdj.Value, result.ForecastIntial.Value);

                                #endregion

                                #region Stock initial

                                result.StockInitial = lastDayStock;

                                #endregion

                                #region Demand
                                var mapDemand = (int?)
                                                (from inv in invAllocDetails
                                                 join preference in mrpPreference
                                                 on new { A = inv.AllocationType, B = "Demand" } equals new { A = preference.AllocationType, B = preference.Mrptype }
                                                 where inv.PlanDate.Value.Date == actDate.Value.Date &&
                                                       invRules.IndexOf(preference.PlanType) > 0
                                                 select inv.PlanQty).Sum(x => x.Value) ?? 0;
                                result.Demand = result.NetDemand + mapDemand;

                                #endregion

                                #region Supply

                                result.Supply = (int?)
                                                (from inv in invAllocDetails
                                                 join preference in mrpPreference
                                                 on new { A = inv.AllocationType, B = "Supply" } equals new { A = preference.AllocationType, B = preference.Mrptype }
                                                 where inv.PlanDate.Value.Date == actDate.Value.Date &&
                                                       invRules.IndexOf(preference.PlanType) > 0
                                                 select inv.PlanQty).Sum(x => x.Value) ?? 0;

                                #endregion

                                #region Stock Ava

                                result.StockAva = result.StockInitial - result.Demand + result.Supply;
                                lastDayStock = result.StockAva ?? 0;

                                #endregion

                                #region Sku + Warehouse + Date

                                result.Sku = Sku;
                                result.Warehouse = Warehouse;
                                result.Date = actDate;

                                #endregion

                                actDate = actDate.Value.AddDays(1);
                            }

                            // Save data
                            this.Actions.PressSave();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                throw new PXOperationCompletedWithErrorException(ex.Message);
            }

        }

        /// <summary> 產生一筆固定資料 </summary>
        public virtual void InitialData()
        {
            string screenIDWODot = this.Accessinfo.ScreenID.ToString().Replace(".", "");
           
            PXDatabase.Insert<LUMMRPProcessResult>(
                                 new PXDataFieldAssign<LUMMRPProcessResult.sku>(GetFixInventoryItem()),
                                 new PXDataFieldAssign<LUMMRPProcessResult.warehouse>(GetFixSiteID()),
                                 new PXDataFieldAssign<LUMMRPProcessResult.date>(this.Accessinfo.BusinessDate),
                                 new PXDataFieldAssign<LUMMRPProcessResult.createdByID>(this.Accessinfo.UserID),
                                 new PXDataFieldAssign<LUMMRPProcessResult.createdByScreenID>(screenIDWODot),
                                 new PXDataFieldAssign<LUMMRPProcessResult.createdDateTime>(this.Accessinfo.BusinessDate),
                                 new PXDataFieldAssign<LUMMRPProcessResult.lastModifiedByID>(this.Accessinfo.UserID),
                                 new PXDataFieldAssign<LUMMRPProcessResult.lastModifiedByScreenID>(screenIDWODot),
                                 new PXDataFieldAssign<LUMMRPProcessResult.lastModifiedDateTime>(this.Accessinfo.BusinessDate));
        }

        /// <summary> 取固定資料的InventoryItem </summary>
        public int? GetFixInventoryItem()
        {
            return SelectFrom<InventoryItem>
                   .Where<InventoryItem.itemStatus.IsEqual<P.AsString>>
                   .View.Select(this, "AC").TopFirst.InventoryID;
        }

        /// <summary> 取固定資料的SiteID </summary>
        public int? GetFixSiteID()
        {
            return SelectFrom<INSite>
                   .Where<INSite.active.IsEqual<True>>
                   .View.Select(this).TopFirst.SiteID;
        }

        public void DeleteData(int? _sku, int? _warehouse)
        {
            PXDatabase.Delete<LUMMRPProcessResult>(
                   new PXDataFieldRestrict<LUMMRPProcessResult.sku>(_sku),
                   new PXDataFieldRestrict<LUMMRPProcessResult.warehouse>(_warehouse));
            this.Transaction.Cache.Clear();
        }
    }

    [Serializable]
    public class MRPFilter : IBqlTable
    {
        [PXDBDate]
        [PXUIField(DisplayName = "Start Date")]
        public virtual DateTime? Date { get; set; }
        public abstract class date : PX.Data.BQL.BqlDateTime.Field<date> { }

        [PXDBInt]
        [StockItem(Required = true)]
        [PXDefault]
        [PXUIField(DisplayName = "SKU", Required = true)]
        public virtual int? Sku { get; set; }
        public abstract class sku : PX.Data.BQL.BqlInt.Field<sku> { }

        [PXDBInt]
        [Site(Required = true)]
        [PXDefault]
        [PXUIField(DisplayName = "Warehouse", Required = true)]
        public virtual int? Warehouse { get; set; }
        public abstract class warehouse : PX.Data.BQL.BqlInt.Field<warehouse> { }

        [PXDBInt]
        [PXDefault]
        [PXUIField(DisplayName = "Item ClassID", Required = true)]
        [PXSelector(typeof(SearchFor<INItemClass.itemClassID>),
            DescriptionField = typeof(INItemClass.itemClassCD),
            SubstituteKey = typeof(INItemClass.itemClassCD))]
        public virtual int? ItemClassID { get; set; }
        public abstract class itemClassID : PX.Data.BQL.BqlInt.Field<itemClassID> { }
    }

}
