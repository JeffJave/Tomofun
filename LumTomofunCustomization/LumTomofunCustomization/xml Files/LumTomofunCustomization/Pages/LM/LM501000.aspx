﻿<%@ Page Language="C#" MasterPageFile="~/MasterPages/FormDetail.master" AutoEventWireup="true" CodeFile="LM501000.aspx.cs" Inherits="Pages_LM501000" %>

<%@ MasterType VirtualPath="~/MasterPages/FormDetail.master" %>

<asp:Content ID="cont1" ContentPlaceHolderID="phDS" runat="Server">
    <px:PXDataSource ID="ds" runat="server" Visible="True" Width="100%"
        TypeName="LumTomofunCustomization.Graph.LUMForecaseUploadProcess"
        PrimaryView="Transaction">
        <CallbackCommands>
        </CallbackCommands>
    </px:PXDataSource>
</asp:Content>

<asp:Content ID="cont3" ContentPlaceHolderID="phG" runat="Server">
    <px:PXGrid SyncPosition="True" ID="grid" runat="server" DataSourceID="ds" Width="100%" Height="150px" SkinID="PrimaryInquire" AllowAutoHide="false">
        <Levels>
            <px:PXGridLevel DataMember="Transaction">
                <Columns>
                    <px:PXGridColumn DataField="Mrptype"></px:PXGridColumn>
                    <px:PXGridColumn DataField="Revision"></px:PXGridColumn>
                    <px:PXGridColumn DataField="Sku"></px:PXGridColumn>
                    <px:PXGridColumn DataField="Company"></px:PXGridColumn>
                    <px:PXGridColumn DataField="Country"></px:PXGridColumn>
                    <px:PXGridColumn DataField="Warehouse" />
                    <px:PXGridColumn DataField="Date" />
                    <px:PXGridColumn DataField="Qty" />
                    <px:PXGridColumn DataField="Week" />
                    <px:PXGridColumn DataField="Qoh" />
                </Columns>
            </px:PXGridLevel>
        </Levels>
        <AutoSize Container="Window" Enabled="True" MinHeight="150" />
        <ActionBar>
        </ActionBar>
        <Mode AllowUpdate="True" AllowUpload="True" />
    </px:PXGrid>

</asp:Content>