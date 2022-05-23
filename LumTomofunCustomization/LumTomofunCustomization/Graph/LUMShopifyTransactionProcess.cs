﻿using LumTomofunCustomization.DAC;
using LUMTomofunCustomization.DAC;
using Newtonsoft.Json;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;
using PX.Objects.SO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PX.Objects.CM;
using PX.Objects.AR;
using LumTomofunCustomization.LUMLibrary;

namespace LumTomofunCustomization.Graph
{
    public class LUMShopifyTransactionProcess : PXGraph<LUMShopifyTransactionProcess>
    {
        public PXSave<LUMShopifyTransData> Save;
        public PXCancel<LUMShopifyTransData> Cancel;
        public PXProcessing<LUMShopifyTransData> ShopifyTransaction;

        public LUMShopifyTransactionProcess()
        {
            ShopifyTransaction.Cache.AllowUpdate = true;
            ShopifyTransaction.SetProcessDelegate(delegate (List<LUMShopifyTransData> list)
            {
                GoProcessing(list);
            });
        }

        #region Method

        /// <summary> 執行Process </summary>
        public static void GoProcessing(List<LUMShopifyTransData> list)
        {
            var graph = CreateInstance<LUMShopifyTransactionProcess>();
            graph.CreateSalesOrder(graph, list);
        }

        /// <summary> Create Sales Order </summary>
        public virtual void CreateSalesOrder(LUMShopifyTransactionProcess baseGraph, List<LUMShopifyTransData> shopifyList)
        {
            PXUIFieldAttribute.SetEnabled<LUMShopifyTransData.isProcessed>(ShopifyTransaction.Cache, null, true);
            foreach (var row in shopifyList)
            {
                try
                {
                    using (PXTransactionScope sc = new PXTransactionScope())
                    {
                        // Marketplace tax calculation
                        var isTaxCalculate = GetMarketplaceTaxCalculation(row.Marketplace);
                        // Create Sales Order Graph
                        var soGraph = PXGraph.CreateInstance<SOOrderEntry>();
                        var spOrder = JsonConvert.DeserializeObject<API_Entity.ShopifyOrder.ShopifyOrderEntity>(row.TransJson);
                        // validation
                        Validation(row);
                        // 判斷是否需要建立Invoice
                        var GoPrepareInvoice = row.FullfillmentStatus?.ToLower() == "fulfilled";
                        // Shopify Order
                        var order = SelectFrom<SOOrder>
                                         .Where<SOOrder.customerOrderNbr.IsEqual<P.AsString>>
                                         .View.Select(this, spOrder.id).TopFirst;
                        // Create Sales Order
                        if (order == null && row.FinancialStatus == "paid")
                        {
                            #region Create Sales Order Header
                            order = soGraph.Document.Cache.CreateInstance() as SOOrder;
                            order.OrderType = "SP";
                            order.CustomerOrderNbr = spOrder.checkout_id.ToString();
                            order.CustomerRefNbr = spOrder.id.ToString();
                            order.OrderDesc = $"Shopify Order #{spOrder.order_number}";
                            order.OrderDate = spOrder.created_at;
                            order.RequestDate = spOrder.created_at;
                            order.CustomerID = GetMarketplaceCustomer(row.Marketplace);
                            order.TermsID = "0000";
                            #region User-Defined
                            // UserDefined - ORDERTYPE
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "ORDERTYPE", spOrder.gateway);
                            // UserDefined - MKTPLACE
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "MKTPLACE", $"Shopify.{row.Marketplace}");
                            // UserDefined - ORDERAMT
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "ORDERAMT", spOrder.current_total_price);
                            // UserDefined - ORDTAAMT
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "ORDTAXAMT", spOrder.current_total_tax);
                            // UserDefined - TAXCOLLECT
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "TAXCOLLECT", 0);
                            // UserDefined -  ORDERTAGS
                            soGraph.Document.Cache.SetValueExt(order, PX.Objects.CS.Messages.Attribute + "ORDERTAGS", spOrder.tags);
                            #endregion
                            // Insert Sales Order
                            soGraph.Document.Insert(order);
                            // Setting Shipping_Address
                            var soAddress = soGraph.Shipping_Address.Current;
                            soAddress.OverrideAddress = true;
                            soAddress.PostalCode = spOrder.shipping_address?.zip;
                            soAddress.CountryID = spOrder.shipping_address?.country_code;
                            soAddress.State = spOrder.shipping_address?.province;
                            soAddress.City = spOrder.shipping_address?.city;
                            soAddress.RevisionID = 1;
                            // Setting Shipping_Contact
                            var soContact = soGraph.Shipping_Contact.Current;
                            soContact.OverrideContact = true;
                            soContact.RevisionID = 1;
                            #endregion

                            #region Set Currency
                            CurrencyInfo info = CurrencyInfoAttribute.SetDefaults<SOOrder.curyInfoID>(soGraph.Document.Cache, soGraph.Document.Current);
                            if (info != null)
                                soGraph.Document.Cache.SetValueExt<SOOrder.curyID>(soGraph.Document.Current, info.CuryID);
                            #endregion

                            #region Create Sales Order Line
                            foreach (var item in spOrder.line_items)
                            {
                                // requires_shipping <> True (Do not import this item)
                                if (!item.requires_shipping)
                                    continue;
                                var line = soGraph.Transactions.Cache.CreateInstance() as SOLine;
                                line.InventoryID = AmazonPublicFunction.GetInvetoryitemID(soGraph, item.sku);
                                if (line.InventoryID == null)
                                    throw new Exception($"can not find Inventory item ID({item.sku})");
                                line.ManualPrice = true;
                                line.OrderQty = item.quantity;
                                line.CuryUnitPrice = decimal.Parse(item.pre_tax_price) / item.quantity;
                                soGraph.Transactions.Insert(line);
                            }
                            // IF SOLine is empty, do not create Sales Order
                            if (soGraph.Transactions.Cache.Inserted.RowCast<SOLine>().Count() == 0)
                                throw new Exception("can not find andy SOLine Item");
                            #endregion

                            #region Create Slaes Order Line for Shipping
                            if (spOrder.shipping_lines.Any(x => decimal.Parse(x.price) > 0))
                            {
                                var soShipLine = soGraph.Transactions.Cache.CreateInstance() as SOLine;
                                soShipLine.InventoryID = GetFeeNonStockItem("Shipping");
                                soShipLine.OrderQty = 1;
                                soShipLine.CuryUnitPrice =
                                    (row.Marketplace == "US" || row.Marketplace == "CA") ?
                                    (decimal?)spOrder.shipping_lines.Sum(x => decimal.Parse(x.price)) :
                                    (decimal?)spOrder.shipping_lines.Sum(x => decimal.Parse(x.price) - x.tax_lines.Sum(y => decimal.Parse(y.price)));
                                soGraph.Transactions.Insert(soShipLine);
                            }
                            #endregion

                            #region Update Tax
                            // Setting SO Tax
                            if (!isTaxCalculate)
                            {
                                soGraph.Taxes.Cache.SetValueExt<SOTaxTran.taxID>(soGraph.Taxes.Current, row.Marketplace + "EC");
                                soGraph.Taxes.Cache.SetValueExt<SOTaxTran.curyTaxAmt>(soGraph.Taxes.Current, spOrder.current_total_tax);

                                soGraph.Document.Cache.SetValueExt<SOOrder.curyTaxTotal>(soGraph.Document.Current, spOrder.current_total_tax);
                                soGraph.Document.Cache.SetValueExt<SOOrder.curyOrderTotal>(soGraph.Document.Current, (soGraph.Document.Current?.CuryOrderTotal ?? 0) + decimal.Parse(spOrder.current_total_tax));
                            }
                            #endregion

                            // Write json into note
                            PXNoteAttribute.SetNote(soGraph.Document.Cache, soGraph.Document.Current, row.TransJson);
                            // Sales Order Save
                            soGraph.Save.Press();
                        }
                        // Assign Document Current
                        else if (GoPrepareInvoice && order != null)
                            soGraph.Document.Current = order;
                        // Prepare Invocie
                        try
                        {
                            // 判斷是否需要Create Invoice
                            var tagConditions = new string[] { "KOL", "REPLACE", "FAAS" };
                            if (Array.IndexOf(tagConditions, spOrder.tags?.ToUpper()) == -1 ||
                                (soGraph.Document.Current.CuryOrderTotal == 0 && Array.IndexOf(tagConditions, spOrder.tags?.ToUpper()) == -1) ||
                                (isTaxCalculate && decimal.Parse(spOrder.current_total_price) != soGraph.Document.Current.CuryTaxTotal.Value - soGraph.Document.Current.CuryTaxTotal) ||
                                (!isTaxCalculate && decimal.Parse(spOrder.current_total_price) - 0 != soGraph.Document.Current.CuryTaxTotal.Value))
                            {
                                GoPrepareInvoice = false;
                            }
                            if (GoPrepareInvoice)
                            {
                                var newAdapter = new PXAdapter(soGraph.Document)
                                {
                                    Searches = new Object[]
                               {
                                    soGraph.Document.Current.OrderType,
                                    soGraph.Document.Current.OrderNbr
                               }
                                };
                                soGraph.PrepareInvoice(newAdapter);
                            }
                        }
                        // Prepare invoice Success
                        catch (PXRedirectRequiredException ex)
                        {
                            SOInvoiceEntry invoiceGraph = ex.Graph as SOInvoiceEntry;
                            // Update docDate
                            invoiceGraph.Document.SetValueExt<ARInvoice.docDate>(invoiceGraph.Document.Current, order.RequestDate);
                            var soTax = SelectFrom<SOTaxTran>
                                        .Where<SOTaxTran.orderNbr.IsEqual<P.AsString>
                                             .And<SOTaxTran.orderType.IsEqual<P.AsString>>>
                                        .View.SelectSingleBound(this, null, soGraph.Document.Current.OrderNbr, soGraph.Document.Current.OrderType)
                                        .TopFirst;
                            if (soTax != null)
                            {
                                // setting Tax
                                invoiceGraph.Taxes.Current = invoiceGraph.Taxes.Select();
                                invoiceGraph.Taxes.SetValueExt<ARTaxTran.curyTaxAmt>(invoiceGraph.Taxes.Current, soTax.CuryTaxAmt);
                                invoiceGraph.Taxes.Cache.MarkUpdated(invoiceGraph.Taxes.Current);
                                // setting Document
                                invoiceGraph.Document.SetValueExt<ARInvoice.curyTaxTotal>(invoiceGraph.Document.Current, soTax.CuryTaxAmt);
                                invoiceGraph.Document.SetValueExt<ARInvoice.curyDocBal>(invoiceGraph.Document.Current, invoiceGraph.Document.Current.CuryDocBal + (soTax.CuryTaxAmt ?? 0));
                                invoiceGraph.Document.SetValueExt<ARInvoice.curyOrigDocAmt>(invoiceGraph.Document.Current, invoiceGraph.Document.Current.CuryOrigDocAmt + (soTax.CuryTaxAmt ?? 0));
                                invoiceGraph.Document.Update(invoiceGraph.Document.Current);
                            }
                            // Save
                            invoiceGraph.Save.Press();
                            // Release Invoice
                            invoiceGraph.releaseFromCreditHold.Press();
                            invoiceGraph.release.Press();
                        }
                        row.IsProcessed = true;
                        row.ErrorMessage = string.Empty;
                        sc.Complete();
                    }
                }
                catch (PXOuterException ex)
                {
                    row.ErrorMessage = ex.InnerMessages[0];
                }
                catch (Exception ex)
                {
                    row.ErrorMessage = ex.Message;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(row.ErrorMessage))
                        PXProcessing.SetError(row.ErrorMessage);
                    baseGraph.ShopifyTransaction.Update(row);
                    // Save 
                    baseGraph.Actions.PressSave();
                }
            }
        }

        /// <summary> 邏輯檢核 </summary>
        public void Validation(LUMShopifyTransData row)
        {
            // Valid Financial Stauts
            if (row.FinancialStatus.ToLower() != "paid")
                throw new Exception("Financial Stauts is not equal Paid!!");
        }

        /// <summary> 取Marketplace 對應 Customer ID </summary>
        public int? GetMarketplaceCustomer(string marketPlace)
            => SelectFrom<LUMShopifyMarketplacePreference>
               .Where<LUMShopifyMarketplacePreference.marketplace.IsEqual<P.AsString>>
               .View.Select(this, marketPlace).TopFirst?.BAccountID;

        /// <summary> 取Marketplace 對應 Tax Calculation </summary>
        public bool GetMarketplaceTaxCalculation(string marketPlace)
            => SelectFrom<LUMShopifyMarketplacePreference>
               .Where<LUMShopifyMarketplacePreference.marketplace.IsEqual<P.AsString>>
               .View.Select(this, marketPlace).TopFirst?.IsTaxCalculation ?? false;

        /// <summary> 取Fee 對應 Non-Stock item ID </summary>
        public int? GetFeeNonStockItem(string fee)
            => SelectFrom<LUMMarketplaceFeePreference>
               .Where<LUMMarketplaceFeePreference.fee.IsEqual<P.AsString>>
               .View.Select(this, fee).TopFirst?.InventoryID;

        #endregion
    }
}
