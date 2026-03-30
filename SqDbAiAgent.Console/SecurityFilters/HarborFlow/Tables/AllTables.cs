using System;
using System.Collections.Generic;
using AiDbClient.ConsoleApp.Tables;
using SqDbAiAgent.ConsoleApp.Tables;
using SqExpress;

namespace SqDbAiAgent.ConsoleApp.SecurityFilters.HarborFlow.Tables
{
    public static class AllTables
    {
        public static readonly IReadOnlyList<TableBase> StaticList = Array.AsReadOnly(BuildAllTableList());
        public static TableBase[] BuildAllTableList() => new TableBase[]
        {
            GetSysdiagrams(Alias.Empty),
            GetBranch(Alias.Empty),
            GetWarehouse(Alias.Empty),
            GetCustomer(Alias.Empty),
            GetEmployee(Alias.Empty),
            GetProductCategory(Alias.Empty),
            GetSalesOrder(Alias.Empty),
            GetProduct(Alias.Empty),
            GetInvoice(Alias.Empty),
            GetSalesOrderLine(Alias.Empty),
            GetShipment(Alias.Empty),
            GetAppRole(Alias.Empty),
            GetAppUser(Alias.Empty),
            GetPayment(Alias.Empty),
            GetShipmentLine(Alias.Empty),
            GetInventoryBalance(Alias.Empty),
            GetAuditLog(Alias.Empty),
            GetUserBranchAccess(Alias.Empty),
            GetUserCustomerAccess(Alias.Empty),
            GetUserRole(Alias.Empty),
            GetUserWarehouseAccess(Alias.Empty)
        };
        public static TableBase[] BuildAllAliasedTableList() => new TableBase[]
        {
            GetSysdiagrams(),
            GetBranch(),
            GetWarehouse(),
            GetCustomer(),
            GetEmployee(),
            GetProductCategory(),
            GetSalesOrder(),
            GetProduct(),
            GetInvoice(),
            GetSalesOrderLine(),
            GetShipment(),
            GetAppRole(),
            GetAppUser(),
            GetPayment(),
            GetShipmentLine(),
            GetInventoryBalance(),
            GetAuditLog(),
            GetUserBranchAccess(),
            GetUserCustomerAccess(),
            GetUserRole(),
            GetUserWarehouseAccess()
        };
        public static TableSysdiagrams GetSysdiagrams(Alias alias) => new TableSysdiagrams(alias);
        public static TableSysdiagrams GetSysdiagrams() => new TableSysdiagrams(Alias.Auto);
        public static TableBranch GetBranch(Alias alias) => new TableBranch(alias);
        public static TableBranch GetBranch() => new TableBranch(Alias.Auto);
        public static TableWarehouse GetWarehouse(Alias alias) => new TableWarehouse(alias);
        public static TableWarehouse GetWarehouse() => new TableWarehouse(Alias.Auto);
        public static TableCustomer GetCustomer(Alias alias) => new TableCustomer(alias);
        public static TableCustomer GetCustomer() => new TableCustomer(Alias.Auto);
        public static TableEmployee GetEmployee(Alias alias) => new TableEmployee(alias);
        public static TableEmployee GetEmployee() => new TableEmployee(Alias.Auto);
        public static TableProductCategory GetProductCategory(Alias alias) => new TableProductCategory(alias);
        public static TableProductCategory GetProductCategory() => new TableProductCategory(Alias.Auto);
        public static TableSalesOrder GetSalesOrder(Alias alias) => new TableSalesOrder(alias);
        public static TableSalesOrder GetSalesOrder() => new TableSalesOrder(Alias.Auto);
        public static TableProduct GetProduct(Alias alias) => new TableProduct(alias);
        public static TableProduct GetProduct() => new TableProduct(Alias.Auto);
        public static TableInvoice GetInvoice(Alias alias) => new TableInvoice(alias);
        public static TableInvoice GetInvoice() => new TableInvoice(Alias.Auto);
        public static TableSalesOrderLine GetSalesOrderLine(Alias alias) => new TableSalesOrderLine(alias);
        public static TableSalesOrderLine GetSalesOrderLine() => new TableSalesOrderLine(Alias.Auto);
        public static TableShipment GetShipment(Alias alias) => new TableShipment(alias);
        public static TableShipment GetShipment() => new TableShipment(Alias.Auto);
        public static TableAppRole GetAppRole(Alias alias) => new TableAppRole(alias);
        public static TableAppRole GetAppRole() => new TableAppRole(Alias.Auto);
        public static TableAppUser GetAppUser(Alias alias) => new TableAppUser(alias);
        public static TableAppUser GetAppUser() => new TableAppUser(Alias.Auto);
        public static TablePayment GetPayment(Alias alias) => new TablePayment(alias);
        public static TablePayment GetPayment() => new TablePayment(Alias.Auto);
        public static TableShipmentLine GetShipmentLine(Alias alias) => new TableShipmentLine(alias);
        public static TableShipmentLine GetShipmentLine() => new TableShipmentLine(Alias.Auto);
        public static TableInventoryBalance GetInventoryBalance(Alias alias) => new TableInventoryBalance(alias);
        public static TableInventoryBalance GetInventoryBalance() => new TableInventoryBalance(Alias.Auto);
        public static TableAuditLog GetAuditLog(Alias alias) => new TableAuditLog(alias);
        public static TableAuditLog GetAuditLog() => new TableAuditLog(Alias.Auto);
        public static TableUserBranchAccess GetUserBranchAccess(Alias alias) => new TableUserBranchAccess(alias);
        public static TableUserBranchAccess GetUserBranchAccess() => new TableUserBranchAccess(Alias.Auto);
        public static TableUserCustomerAccess GetUserCustomerAccess(Alias alias) => new TableUserCustomerAccess(alias);
        public static TableUserCustomerAccess GetUserCustomerAccess() => new TableUserCustomerAccess(Alias.Auto);
        public static TableUserRole GetUserRole(Alias alias) => new TableUserRole(alias);
        public static TableUserRole GetUserRole() => new TableUserRole(Alias.Auto);
        public static TableUserWarehouseAccess GetUserWarehouseAccess(Alias alias) => new TableUserWarehouseAccess(alias);
        public static TableUserWarehouseAccess GetUserWarehouseAccess() => new TableUserWarehouseAccess(Alias.Auto);
    }
}
