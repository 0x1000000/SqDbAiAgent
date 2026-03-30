SET NOCOUNT ON;
GO

USE master;
GO

IF DB_ID(N'HarborFlow') IS NOT NULL
BEGIN
    ALTER DATABASE HarborFlow SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE HarborFlow;
END
GO

CREATE DATABASE HarborFlow;
GO

USE HarborFlow;
GO

/*
Schema layout:
- sec: security and permission data, not intended for AI prompt exposure
- ref: reference/read-oriented business data, safe for AI-generated SELECT queries
- ops: operational transaction data, readable by AI and later candidates for controlled writes
*/

IF SCHEMA_ID('sec') IS NULL EXEC('CREATE SCHEMA sec');
IF SCHEMA_ID('ref') IS NULL EXEC('CREATE SCHEMA ref');
IF SCHEMA_ID('ops') IS NULL EXEC('CREATE SCHEMA ops');
GO

CREATE TABLE ref.Branch
(
    BranchId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Branch PRIMARY KEY,
    BranchCode       NVARCHAR(12) NOT NULL,
    BranchName       NVARCHAR(100) NOT NULL,
    Region           NVARCHAR(50) NOT NULL,
    City             NVARCHAR(50) NOT NULL,
    IsActive         BIT NOT NULL CONSTRAINT DF_Branch_IsActive DEFAULT (1),
    CreatedUtc       DATETIME2(0) NOT NULL CONSTRAINT DF_Branch_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Branch_BranchCode UNIQUE (BranchCode)
);
GO

CREATE TABLE ref.Employee
(
    EmployeeId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Employee PRIMARY KEY,
    BranchId         INT NOT NULL,
    EmployeeCode     NVARCHAR(20) NOT NULL,
    FirstName        NVARCHAR(50) NOT NULL,
    LastName         NVARCHAR(50) NOT NULL,
    Title            NVARCHAR(80) NOT NULL,
    Email            NVARCHAR(120) NOT NULL,
    IsSalesRep       BIT NOT NULL CONSTRAINT DF_Employee_IsSalesRep DEFAULT (0),
    IsActive         BIT NOT NULL CONSTRAINT DF_Employee_IsActive DEFAULT (1),
    CreatedUtc       DATETIME2(0) NOT NULL CONSTRAINT DF_Employee_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_Employee_Branch FOREIGN KEY (BranchId) REFERENCES ref.Branch(BranchId),
    CONSTRAINT UQ_Employee_EmployeeCode UNIQUE (EmployeeCode),
    CONSTRAINT UQ_Employee_Email UNIQUE (Email)
);
GO

CREATE TABLE ref.Customer
(
    CustomerId           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    BranchId             INT NOT NULL,
    CustomerCode         NVARCHAR(20) NOT NULL,
    CustomerName         NVARCHAR(120) NOT NULL,
    CustomerType         NVARCHAR(20) NOT NULL,
    City                 NVARCHAR(50) NOT NULL,
    Province             NVARCHAR(50) NOT NULL,
    CreditLimit          DECIMAL(12,2) NOT NULL,
    IsActive             BIT NOT NULL CONSTRAINT DF_Customer_IsActive DEFAULT (1),
    PreferredWarehouseId INT NULL,
    CreatedUtc           DATETIME2(0) NOT NULL CONSTRAINT DF_Customer_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_Customer_Branch FOREIGN KEY (BranchId) REFERENCES ref.Branch(BranchId),
    CONSTRAINT UQ_Customer_CustomerCode UNIQUE (CustomerCode),
    CONSTRAINT CK_Customer_Type CHECK (CustomerType IN ('Retail','Wholesale','Government'))
);
GO

CREATE TABLE ref.ProductCategory
(
    ProductCategoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductCategory PRIMARY KEY,
    CategoryCode      NVARCHAR(20) NOT NULL,
    CategoryName      NVARCHAR(80) NOT NULL,
    IsActive          BIT NOT NULL CONSTRAINT DF_ProductCategory_IsActive DEFAULT (1),
    CONSTRAINT UQ_ProductCategory_CategoryCode UNIQUE (CategoryCode)
);
GO

CREATE TABLE ref.Product
(
    ProductId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Product PRIMARY KEY,
    ProductCategoryId  INT NOT NULL,
    Sku                NVARCHAR(30) NOT NULL,
    ProductName        NVARCHAR(120) NOT NULL,
    UnitPrice          DECIMAL(12,2) NOT NULL,
    UnitCost           DECIMAL(12,2) NOT NULL,
    IsDiscontinued     BIT NOT NULL CONSTRAINT DF_Product_IsDiscontinued DEFAULT (0),
    CreatedUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_Product_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_Product_ProductCategory FOREIGN KEY (ProductCategoryId) REFERENCES ref.ProductCategory(ProductCategoryId),
    CONSTRAINT UQ_Product_Sku UNIQUE (Sku),
    CONSTRAINT CK_Product_Price CHECK (UnitPrice >= 0),
    CONSTRAINT CK_Product_Cost CHECK (UnitCost >= 0)
);
GO

CREATE TABLE ref.Warehouse
(
    WarehouseId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouse PRIMARY KEY,
    BranchId         INT NOT NULL,
    WarehouseCode    NVARCHAR(20) NOT NULL,
    WarehouseName    NVARCHAR(100) NOT NULL,
    City             NVARCHAR(50) NOT NULL,
    IsPrimary        BIT NOT NULL CONSTRAINT DF_Warehouse_IsPrimary DEFAULT (0),
    IsActive         BIT NOT NULL CONSTRAINT DF_Warehouse_IsActive DEFAULT (1),
    CONSTRAINT FK_Warehouse_Branch FOREIGN KEY (BranchId) REFERENCES ref.Branch(BranchId),
    CONSTRAINT UQ_Warehouse_WarehouseCode UNIQUE (WarehouseCode)
);
GO

ALTER TABLE ref.Customer
ADD CONSTRAINT FK_Customer_PreferredWarehouse FOREIGN KEY (PreferredWarehouseId) REFERENCES ref.Warehouse(WarehouseId);
GO

CREATE TABLE ref.InventoryBalance
(
    WarehouseId      INT NOT NULL,
    ProductId        INT NOT NULL,
    QuantityOnHand   INT NOT NULL,
    QuantityReserved INT NOT NULL,
    ReorderLevel     INT NOT NULL,
    LastCountedUtc   DATETIME2(0) NOT NULL,
    CONSTRAINT PK_InventoryBalance PRIMARY KEY (WarehouseId, ProductId),
    CONSTRAINT FK_InventoryBalance_Warehouse FOREIGN KEY (WarehouseId) REFERENCES ref.Warehouse(WarehouseId),
    CONSTRAINT FK_InventoryBalance_Product FOREIGN KEY (ProductId) REFERENCES ref.Product(ProductId),
    CONSTRAINT CK_InventoryBalance_QtyOnHand CHECK (QuantityOnHand >= 0),
    CONSTRAINT CK_InventoryBalance_QtyReserved CHECK (QuantityReserved >= 0),
    CONSTRAINT CK_InventoryBalance_ReorderLevel CHECK (ReorderLevel >= 0)
);
GO

CREATE TABLE ops.SalesOrder
(
    SalesOrderId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOrder PRIMARY KEY,
    OrderNumber       NVARCHAR(20) NOT NULL,
    BranchId          INT NOT NULL,
    CustomerId        INT NOT NULL,
    SalesRepId        INT NOT NULL,
    OrderStatus       NVARCHAR(20) NOT NULL,
    OrderDate         DATE NOT NULL,
    RequiredDate      DATE NULL,
    CurrencyCode      CHAR(3) NOT NULL CONSTRAINT DF_SalesOrder_CurrencyCode DEFAULT ('CAD'),
    Notes             NVARCHAR(200) NULL,
    CreatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_SalesOrder_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_SalesOrder_Branch FOREIGN KEY (BranchId) REFERENCES ref.Branch(BranchId),
    CONSTRAINT FK_SalesOrder_Customer FOREIGN KEY (CustomerId) REFERENCES ref.Customer(CustomerId),
    CONSTRAINT FK_SalesOrder_Employee FOREIGN KEY (SalesRepId) REFERENCES ref.Employee(EmployeeId),
    CONSTRAINT UQ_SalesOrder_OrderNumber UNIQUE (OrderNumber),
    CONSTRAINT CK_SalesOrder_Status CHECK (OrderStatus IN ('Draft','Submitted','Approved','Packed','Shipped','Closed','Cancelled'))
);
GO

CREATE TABLE ops.SalesOrderLine
(
    SalesOrderLineId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOrderLine PRIMARY KEY,
    SalesOrderId      INT NOT NULL,
    LineNumber        INT NOT NULL,
    ProductId         INT NOT NULL,
    WarehouseId       INT NOT NULL,
    Quantity          INT NOT NULL,
    UnitPrice         DECIMAL(12,2) NOT NULL,
    DiscountPercent   DECIMAL(5,2) NOT NULL CONSTRAINT DF_SalesOrderLine_DiscountPercent DEFAULT (0),
    LineStatus        NVARCHAR(20) NOT NULL CONSTRAINT DF_SalesOrderLine_LineStatus DEFAULT ('Open'),
    CONSTRAINT FK_SalesOrderLine_SalesOrder FOREIGN KEY (SalesOrderId) REFERENCES ops.SalesOrder(SalesOrderId),
    CONSTRAINT FK_SalesOrderLine_Product FOREIGN KEY (ProductId) REFERENCES ref.Product(ProductId),
    CONSTRAINT FK_SalesOrderLine_Warehouse FOREIGN KEY (WarehouseId) REFERENCES ref.Warehouse(WarehouseId),
    CONSTRAINT UQ_SalesOrderLine_Order_Line UNIQUE (SalesOrderId, LineNumber),
    CONSTRAINT CK_SalesOrderLine_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_SalesOrderLine_UnitPrice CHECK (UnitPrice >= 0),
    CONSTRAINT CK_SalesOrderLine_Discount CHECK (DiscountPercent >= 0 AND DiscountPercent <= 100),
    CONSTRAINT CK_SalesOrderLine_Status CHECK (LineStatus IN ('Open','Allocated','Shipped','Cancelled'))
);
GO

CREATE TABLE ops.Shipment
(
    ShipmentId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shipment PRIMARY KEY,
    ShipmentNumber    NVARCHAR(20) NOT NULL,
    SalesOrderId      INT NOT NULL,
    WarehouseId       INT NOT NULL,
    ShipmentDate      DATE NOT NULL,
    CarrierName       NVARCHAR(80) NOT NULL,
    TrackingNumber    NVARCHAR(50) NULL,
    ShipmentStatus    NVARCHAR(20) NOT NULL,
    CONSTRAINT FK_Shipment_SalesOrder FOREIGN KEY (SalesOrderId) REFERENCES ops.SalesOrder(SalesOrderId),
    CONSTRAINT FK_Shipment_Warehouse FOREIGN KEY (WarehouseId) REFERENCES ref.Warehouse(WarehouseId),
    CONSTRAINT UQ_Shipment_ShipmentNumber UNIQUE (ShipmentNumber),
    CONSTRAINT CK_Shipment_Status CHECK (ShipmentStatus IN ('Packed','Shipped','Delivered','Returned'))
);
GO

CREATE TABLE ops.ShipmentLine
(
    ShipmentLineId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShipmentLine PRIMARY KEY,
    ShipmentId        INT NOT NULL,
    SalesOrderLineId  INT NOT NULL,
    QuantityShipped   INT NOT NULL,
    CONSTRAINT FK_ShipmentLine_Shipment FOREIGN KEY (ShipmentId) REFERENCES ops.Shipment(ShipmentId),
    CONSTRAINT FK_ShipmentLine_SalesOrderLine FOREIGN KEY (SalesOrderLineId) REFERENCES ops.SalesOrderLine(SalesOrderLineId),
    CONSTRAINT UQ_ShipmentLine UNIQUE (ShipmentId, SalesOrderLineId),
    CONSTRAINT CK_ShipmentLine_Quantity CHECK (QuantityShipped > 0)
);
GO

CREATE TABLE ops.Invoice
(
    InvoiceId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY,
    InvoiceNumber     NVARCHAR(20) NOT NULL,
    SalesOrderId      INT NOT NULL,
    InvoiceDate       DATE NOT NULL,
    InvoiceStatus     NVARCHAR(20) NOT NULL,
    SubtotalAmount    DECIMAL(12,2) NOT NULL,
    TaxAmount         DECIMAL(12,2) NOT NULL,
    TotalAmount       DECIMAL(12,2) NOT NULL,
    DueDate           DATE NOT NULL,
    CONSTRAINT FK_Invoice_SalesOrder FOREIGN KEY (SalesOrderId) REFERENCES ops.SalesOrder(SalesOrderId),
    CONSTRAINT UQ_Invoice_InvoiceNumber UNIQUE (InvoiceNumber),
    CONSTRAINT CK_Invoice_Status CHECK (InvoiceStatus IN ('Open','Paid','PartiallyPaid','Cancelled'))
);
GO

CREATE TABLE ops.Payment
(
    PaymentId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payment PRIMARY KEY,
    InvoiceId         INT NOT NULL,
    PaymentDate       DATE NOT NULL,
    PaymentMethod     NVARCHAR(20) NOT NULL,
    Amount            DECIMAL(12,2) NOT NULL,
    ReferenceNumber   NVARCHAR(40) NULL,
    CONSTRAINT FK_Payment_Invoice FOREIGN KEY (InvoiceId) REFERENCES ops.Invoice(InvoiceId),
    CONSTRAINT CK_Payment_Method CHECK (PaymentMethod IN ('Wire','Card','Cheque','Cash')),
    CONSTRAINT CK_Payment_Amount CHECK (Amount > 0)
);
GO

CREATE TABLE sec.AppUser
(
    AppUserId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,
    LoginName         NVARCHAR(50) NOT NULL,
    DisplayName       NVARCHAR(100) NOT NULL,
    EmployeeId        INT NULL,
    IsActive          BIT NOT NULL CONSTRAINT DF_AppUser_IsActive DEFAULT (1),
    CreatedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_AppUser_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_AppUser_Employee FOREIGN KEY (EmployeeId) REFERENCES ref.Employee(EmployeeId),
    CONSTRAINT UQ_AppUser_LoginName UNIQUE (LoginName)
);
GO

CREATE TABLE sec.AppRole
(
    AppRoleId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppRole PRIMARY KEY,
    RoleName          NVARCHAR(50) NOT NULL,
    Description       NVARCHAR(200) NULL,
    CONSTRAINT UQ_AppRole_RoleName UNIQUE (RoleName)
);
GO

CREATE TABLE sec.UserRole
(
    AppUserId         INT NOT NULL,
    AppRoleId         INT NOT NULL,
    GrantedUtc        DATETIME2(0) NOT NULL CONSTRAINT DF_UserRole_GrantedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_UserRole PRIMARY KEY (AppUserId, AppRoleId),
    CONSTRAINT FK_UserRole_AppUser FOREIGN KEY (AppUserId) REFERENCES sec.AppUser(AppUserId),
    CONSTRAINT FK_UserRole_AppRole FOREIGN KEY (AppRoleId) REFERENCES sec.AppRole(AppRoleId)
);
GO

CREATE TABLE sec.UserBranchAccess
(
    AppUserId         INT NOT NULL,
    BranchId          INT NOT NULL,
    CanRead           BIT NOT NULL,
    CanWrite          BIT NOT NULL,
    CONSTRAINT PK_UserBranchAccess PRIMARY KEY (AppUserId, BranchId),
    CONSTRAINT FK_UserBranchAccess_AppUser FOREIGN KEY (AppUserId) REFERENCES sec.AppUser(AppUserId),
    CONSTRAINT FK_UserBranchAccess_Branch FOREIGN KEY (BranchId) REFERENCES ref.Branch(BranchId)
);
GO

CREATE TABLE sec.UserCustomerAccess
(
    AppUserId         INT NOT NULL,
    CustomerId        INT NOT NULL,
    CanRead           BIT NOT NULL,
    CanWrite          BIT NOT NULL,
    CONSTRAINT PK_UserCustomerAccess PRIMARY KEY (AppUserId, CustomerId),
    CONSTRAINT FK_UserCustomerAccess_AppUser FOREIGN KEY (AppUserId) REFERENCES sec.AppUser(AppUserId),
    CONSTRAINT FK_UserCustomerAccess_Customer FOREIGN KEY (CustomerId) REFERENCES ref.Customer(CustomerId)
);
GO

CREATE TABLE sec.UserWarehouseAccess
(
    AppUserId         INT NOT NULL,
    WarehouseId       INT NOT NULL,
    CanRead           BIT NOT NULL,
    CanWrite          BIT NOT NULL,
    CONSTRAINT PK_UserWarehouseAccess PRIMARY KEY (AppUserId, WarehouseId),
    CONSTRAINT FK_UserWarehouseAccess_AppUser FOREIGN KEY (AppUserId) REFERENCES sec.AppUser(AppUserId),
    CONSTRAINT FK_UserWarehouseAccess_Warehouse FOREIGN KEY (WarehouseId) REFERENCES ref.Warehouse(WarehouseId)
);
GO

CREATE TABLE sec.AuditLog
(
    AuditLogId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    AppUserId         INT NOT NULL,
    EventUtc          DATETIME2(0) NOT NULL CONSTRAINT DF_AuditLog_EventUtc DEFAULT (SYSUTCDATETIME()),
    EventType         NVARCHAR(40) NOT NULL,
    EntityName        NVARCHAR(80) NOT NULL,
    EntityId          NVARCHAR(80) NOT NULL,
    Details           NVARCHAR(400) NULL,
    CONSTRAINT FK_AuditLog_AppUser FOREIGN KEY (AppUserId) REFERENCES sec.AppUser(AppUserId)
);
GO

CREATE INDEX IX_Employee_BranchId ON ref.Employee(BranchId);
CREATE INDEX IX_Customer_BranchId ON ref.Customer(BranchId);
CREATE INDEX IX_Customer_Name ON ref.Customer(CustomerName);
CREATE INDEX IX_Product_ProductCategoryId ON ref.Product(ProductCategoryId);
CREATE INDEX IX_Product_ProductName ON ref.Product(ProductName);
CREATE INDEX IX_Warehouse_BranchId ON ref.Warehouse(BranchId);
CREATE INDEX IX_InventoryBalance_ProductId ON ref.InventoryBalance(ProductId);
CREATE INDEX IX_SalesOrder_BranchId_OrderDate ON ops.SalesOrder(BranchId, OrderDate);
CREATE INDEX IX_SalesOrder_CustomerId_OrderDate ON ops.SalesOrder(CustomerId, OrderDate);
CREATE INDEX IX_SalesOrder_SalesRepId ON ops.SalesOrder(SalesRepId);
CREATE INDEX IX_SalesOrder_OrderStatus ON ops.SalesOrder(OrderStatus);
CREATE INDEX IX_SalesOrderLine_ProductId ON ops.SalesOrderLine(ProductId);
CREATE INDEX IX_SalesOrderLine_WarehouseId ON ops.SalesOrderLine(WarehouseId);
CREATE INDEX IX_Shipment_SalesOrderId ON ops.Shipment(SalesOrderId);
CREATE INDEX IX_Invoice_SalesOrderId ON ops.Invoice(SalesOrderId);
CREATE INDEX IX_Invoice_Status_DueDate ON ops.Invoice(InvoiceStatus, DueDate);
CREATE INDEX IX_Payment_InvoiceId ON ops.Payment(InvoiceId);
CREATE INDEX IX_UserBranchAccess_BranchId ON sec.UserBranchAccess(BranchId);
CREATE INDEX IX_UserCustomerAccess_CustomerId ON sec.UserCustomerAccess(CustomerId);
CREATE INDEX IX_UserWarehouseAccess_WarehouseId ON sec.UserWarehouseAccess(WarehouseId);
CREATE INDEX IX_AuditLog_AppUserId_EventUtc ON sec.AuditLog(AppUserId, EventUtc);
GO

INSERT INTO ref.Branch (BranchCode, BranchName, Region, City)
VALUES
    ('TOR', 'Toronto Branch', 'Ontario', 'Toronto'),
    ('OTT', 'Ottawa Branch', 'Ontario', 'Ottawa'),
    ('CAL', 'Calgary Branch', 'Alberta', 'Calgary');
GO

INSERT INTO ref.Employee (BranchId, EmployeeCode, FirstName, LastName, Title, Email, IsSalesRep)
VALUES
    (1, 'E1001', 'Mia', 'Chen', 'Branch Manager', 'mia.chen@demo.local', 0),
    (1, 'E1002', 'Lucas', 'Wright', 'Sales Representative', 'lucas.wright@demo.local', 1),
    (1, 'E1003', 'Sofia', 'Patel', 'Sales Representative', 'sofia.patel@demo.local', 1),
    (2, 'E2001', 'Noah', 'Martin', 'Branch Manager', 'noah.martin@demo.local', 0),
    (2, 'E2002', 'Emma', 'Roy', 'Sales Representative', 'emma.roy@demo.local', 1),
    (3, 'E3001', 'Liam', 'Singh', 'Branch Manager', 'liam.singh@demo.local', 0),
    (3, 'E3002', 'Ava', 'Brooks', 'Sales Representative', 'ava.brooks@demo.local', 1);
GO

INSERT INTO ref.ProductCategory (CategoryCode, CategoryName)
VALUES
    ('LAP', 'Laptops'),
    ('MON', 'Monitors'),
    ('NET', 'Networking'),
    ('ACC', 'Accessories');
GO

INSERT INTO ref.Product (ProductCategoryId, Sku, ProductName, UnitPrice, UnitCost)
VALUES
    (1, 'LT-100', 'ApexBook 14', 1199.00, 860.00),
    (1, 'LT-200', 'ApexBook 15 Pro', 1599.00, 1180.00),
    (1, 'LT-300', 'TerraLite 13', 999.00, 710.00),
    (2, 'MN-100', 'ViewMax 24', 239.00, 150.00),
    (2, 'MN-200', 'ViewMax 27', 329.00, 210.00),
    (2, 'MN-300', 'UltraWide 34', 649.00, 450.00),
    (3, 'NW-100', 'EdgeSwitch 24', 899.00, 620.00),
    (3, 'NW-200', 'SecureRouter X', 499.00, 320.00),
    (4, 'AC-100', 'Docking Station USB-C', 189.00, 110.00),
    (4, 'AC-200', 'Wireless Keyboard Combo', 79.00, 38.00),
    (4, 'AC-300', 'Noise Cancel Headset', 149.00, 88.00),
    (4, 'AC-400', 'Laptop Backpack Pro', 99.00, 54.00);
GO

INSERT INTO ref.Warehouse (BranchId, WarehouseCode, WarehouseName, City, IsPrimary)
VALUES
    (1, 'TOR-MAIN', 'Toronto Main Warehouse', 'Toronto', 1),
    (1, 'TOR-EAST', 'Toronto East Warehouse', 'Toronto', 0),
    (2, 'OTT-MAIN', 'Ottawa Main Warehouse', 'Ottawa', 1),
    (3, 'CAL-MAIN', 'Calgary Main Warehouse', 'Calgary', 1);
GO

INSERT INTO ref.Customer (BranchId, CustomerCode, CustomerName, CustomerType, City, Province, CreditLimit, PreferredWarehouseId)
VALUES
    (1, 'C1001', 'Maple Health Group', 'Wholesale', 'Toronto', 'Ontario', 75000.00, 1),
    (1, 'C1002', 'Northern Schools Board', 'Government', 'Toronto', 'Ontario', 150000.00, 1),
    (1, 'C1003', 'Bright Retail Ltd', 'Retail', 'Mississauga', 'Ontario', 25000.00, 2),
    (2, 'C2001', 'Capital Office Supply', 'Wholesale', 'Ottawa', 'Ontario', 60000.00, 3),
    (2, 'C2002', 'Ottawa Tech Hub', 'Retail', 'Ottawa', 'Ontario', 20000.00, 3),
    (3, 'C3001', 'Prairie Energy Corp', 'Wholesale', 'Calgary', 'Alberta', 120000.00, 4),
    (3, 'C3002', 'Rocky Mountain College', 'Government', 'Calgary', 'Alberta', 90000.00, 4),
    (3, 'C3003', 'Summit Co-Working', 'Retail', 'Calgary', 'Alberta', 18000.00, 4);
GO

INSERT INTO ref.InventoryBalance (WarehouseId, ProductId, QuantityOnHand, QuantityReserved, ReorderLevel, LastCountedUtc)
VALUES
    (1, 1, 45, 6, 10, '2026-03-01'),
    (1, 2, 28, 8, 8, '2026-03-01'),
    (1, 4, 120, 18, 20, '2026-03-01'),
    (1, 5, 85, 12, 15, '2026-03-01'),
    (1, 7, 16, 2, 5, '2026-03-01'),
    (1, 9, 55, 10, 10, '2026-03-01'),
    (1, 10, 140, 20, 25, '2026-03-01'),
    (2, 1, 18, 4, 8, '2026-03-02'),
    (2, 3, 25, 3, 8, '2026-03-02'),
    (2, 6, 12, 1, 4, '2026-03-02'),
    (2, 11, 40, 5, 8, '2026-03-02'),
    (3, 2, 14, 5, 6, '2026-03-03'),
    (3, 4, 60, 6, 10, '2026-03-03'),
    (3, 8, 22, 4, 6, '2026-03-03'),
    (3, 9, 35, 6, 8, '2026-03-03'),
    (4, 1, 20, 2, 8, '2026-03-04'),
    (4, 5, 42, 5, 10, '2026-03-04'),
    (4, 7, 13, 2, 5, '2026-03-04'),
    (4, 10, 70, 7, 15, '2026-03-04'),
    (4, 12, 34, 3, 6, '2026-03-04');
GO

INSERT INTO ops.SalesOrder (OrderNumber, BranchId, CustomerId, SalesRepId, OrderStatus, OrderDate, RequiredDate, Notes)
VALUES
    ('SO-2026-0001', 1, 1, 2, 'Shipped',   '2026-01-10', '2026-01-14', 'Urgent clinic refresh'),
    ('SO-2026-0002', 1, 2, 3, 'Approved',  '2026-01-18', '2026-01-25', 'School lab upgrade'),
    ('SO-2026-0003', 1, 3, 2, 'Draft',     '2026-02-02', '2026-02-08', 'Retail replenishment'),
    ('SO-2026-0004', 2, 4, 5, 'Closed',    '2026-01-12', '2026-01-20', 'Office expansion'),
    ('SO-2026-0005', 2, 5, 5, 'Packed',    '2026-02-14', '2026-02-18', 'Coworking setup'),
    ('SO-2026-0006', 3, 6, 7, 'Shipped',   '2026-01-28', '2026-02-03', 'Branch modernization'),
    ('SO-2026-0007', 3, 7, 7, 'Approved',  '2026-02-05', '2026-02-10', 'Campus display rollout'),
    ('SO-2026-0008', 3, 8, 7, 'Draft',     '2026-02-20', '2026-02-28', 'New employee kits'),
    ('SO-2026-0009', 1, 1, 3, 'Submitted', '2026-03-03', '2026-03-07', 'Additional docking stations'),
    ('SO-2026-0010', 2, 4, 5, 'Cancelled', '2026-03-04', '2026-03-15', 'Customer budget hold');
GO

INSERT INTO ops.SalesOrderLine (SalesOrderId, LineNumber, ProductId, WarehouseId, Quantity, UnitPrice, DiscountPercent, LineStatus)
VALUES
    (1, 1, 1, 1, 10, 1150.00, 2.00, 'Shipped'),
    (1, 2, 9, 1, 10, 179.00, 0.00, 'Shipped'),
    (2, 1, 2, 1, 15, 1540.00, 3.00, 'Allocated'),
    (2, 2, 4, 1, 30, 225.00, 1.50, 'Allocated'),
    (3, 1, 10, 2, 25, 75.00, 0.00, 'Open'),
    (3, 2, 12, 2, 12, 95.00, 0.00, 'Open'),
    (4, 1, 5, 3, 20, 315.00, 2.50, 'Shipped'),
    (4, 2, 8, 3, 8, 485.00, 0.00, 'Shipped'),
    (5, 1, 3, 3, 6, 970.00, 0.00, 'Allocated'),
    (5, 2, 11, 3, 10, 140.00, 0.00, 'Allocated'),
    (6, 1, 7, 4, 5, 875.00, 1.50, 'Shipped'),
    (6, 2, 1, 4, 4, 1175.00, 2.00, 'Shipped'),
    (7, 1, 6, 4, 7, 620.00, 2.00, 'Allocated'),
    (7, 2, 10, 4, 20, 76.00, 0.00, 'Allocated'),
    (8, 1, 2, 4, 3, 1560.00, 0.00, 'Open'),
    (8, 2, 9, 4, 3, 185.00, 0.00, 'Open'),
    (9, 1, 9, 1, 18, 181.00, 0.00, 'Open'),
    (10, 1, 5, 3, 12, 320.00, 0.00, 'Cancelled');
GO

INSERT INTO ops.Shipment (ShipmentNumber, SalesOrderId, WarehouseId, ShipmentDate, CarrierName, TrackingNumber, ShipmentStatus)
VALUES
    ('SH-2026-0001', 1, 1, '2026-01-12', 'Maple Freight', 'MF-10001', 'Delivered'),
    ('SH-2026-0002', 4, 3, '2026-01-18', 'Capital Courier', 'CC-20001', 'Delivered'),
    ('SH-2026-0003', 6, 4, '2026-02-01', 'Prairie Express', 'PE-30001', 'Shipped');
GO

INSERT INTO ops.ShipmentLine (ShipmentId, SalesOrderLineId, QuantityShipped)
VALUES
    (1, 1, 10),
    (1, 2, 10),
    (2, 7, 20),
    (2, 8, 8),
    (3, 11, 5),
    (3, 12, 4);
GO

INSERT INTO ops.Invoice (InvoiceNumber, SalesOrderId, InvoiceDate, InvoiceStatus, SubtotalAmount, TaxAmount, TotalAmount, DueDate)
VALUES
    ('INV-2026-0001', 1, '2026-01-13', 'Paid',         13290.00, 1727.70, 15017.70, '2026-02-12'),
    ('INV-2026-0002', 4, '2026-01-19', 'Paid',         10180.00, 1323.40, 11503.40, '2026-02-18'),
    ('INV-2026-0003', 6, '2026-02-02', 'PartiallyPaid', 9025.00, 1173.25, 10198.25, '2026-03-04'),
    ('INV-2026-0004', 2, '2026-01-20', 'Open',         29625.00, 3851.25, 33476.25, '2026-02-19');
GO

INSERT INTO ops.Payment (InvoiceId, PaymentDate, PaymentMethod, Amount, ReferenceNumber)
VALUES
    (1, '2026-01-28', 'Wire',   15017.70, 'WIRE-9001'),
    (2, '2026-02-05', 'Card',   11503.40, 'CARD-4401'),
    (3, '2026-02-20', 'Wire',    5000.00, 'WIRE-9012');
GO

INSERT INTO sec.AppRole (RoleName, Description)
VALUES
    ('SalesRep', 'Can read and create orders in assigned scope'),
    ('BranchManager', 'Can read and write across assigned branch'),
    ('Finance', 'Can read invoices and payments'),
    ('WarehouseLead', 'Can read warehouse and shipment data');
GO

INSERT INTO sec.AppUser (LoginName, DisplayName, EmployeeId)
VALUES
    ('lucas', 'Lucas Wright', 2),
    ('sofia', 'Sofia Patel', 3),
    ('emma', 'Emma Roy', 5),
    ('ava', 'Ava Brooks', 7),
    ('mia', 'Mia Chen', 1),
    ('fin.anne', 'Anne Finance', NULL),
    ('wh.tom', 'Tom Warehouse', NULL);
GO

INSERT INTO sec.UserRole (AppUserId, AppRoleId)
VALUES
    (1, 1),
    (2, 1),
    (3, 1),
    (4, 1),
    (5, 2),
    (6, 3),
    (7, 4);
GO

INSERT INTO sec.UserBranchAccess (AppUserId, BranchId, CanRead, CanWrite)
VALUES
    (1, 1, 1, 1),
    (2, 1, 1, 1),
    (3, 2, 1, 1),
    (4, 3, 1, 1),
    (5, 1, 1, 1),
    (6, 1, 1, 0),
    (6, 2, 1, 0),
    (6, 3, 1, 0),
    (7, 1, 1, 0),
    (7, 2, 1, 0),
    (7, 3, 1, 0);
GO

INSERT INTO sec.UserCustomerAccess (AppUserId, CustomerId, CanRead, CanWrite)
VALUES
    (1, 1, 1, 1),
    (1, 3, 1, 1),
    (2, 2, 1, 1),
    (3, 4, 1, 1),
    (3, 5, 1, 1),
    (4, 6, 1, 1),
    (4, 7, 1, 1),
    (4, 8, 1, 1),
    (6, 1, 1, 0),
    (6, 4, 1, 0),
    (6, 6, 1, 0);
GO

INSERT INTO sec.UserWarehouseAccess (AppUserId, WarehouseId, CanRead, CanWrite)
VALUES
    (1, 1, 1, 0),
    (1, 2, 1, 0),
    (2, 1, 1, 0),
    (3, 3, 1, 0),
    (4, 4, 1, 0),
    (5, 1, 1, 1),
    (5, 2, 1, 1),
    (7, 1, 1, 1),
    (7, 2, 1, 1),
    (7, 3, 1, 1),
    (7, 4, 1, 1);
GO

INSERT INTO sec.AuditLog (AppUserId, EventUtc, EventType, EntityName, EntityId, Details)
VALUES
    (1, '2026-03-01T10:15:00', 'ORDER_READ', 'SalesOrder', '9', 'Viewed submitted order'),
    (2, '2026-03-02T09:30:00', 'ORDER_CREATE', 'SalesOrder', '9', 'Created order header'),
    (5, '2026-03-02T10:00:00', 'ORDER_APPROVE', 'SalesOrder', '2', 'Approved school order'),
    (6, '2026-03-03T11:20:00', 'INVOICE_READ', 'Invoice', '4', 'Reviewed outstanding invoice'),
    (7, '2026-03-04T08:45:00', 'SHIPMENT_UPDATE', 'Shipment', '3', 'Updated shipment tracking');
GO

INSERT INTO ref.Employee (BranchId, EmployeeCode, FirstName, LastName, Title, Email, IsSalesRep)
VALUES
    (1, 'E1004', 'Oliver', 'Grant', 'Sales Representative', 'oliver.grant@demo.local', 1),
    (1, 'E1005', 'Chloe', 'Bennett', 'Account Executive', 'chloe.bennett@demo.local', 1),
    (2, 'E2003', 'Jacob', 'Lewis', 'Sales Representative', 'jacob.lewis@demo.local', 1),
    (2, 'E2004', 'Grace', 'Adams', 'Account Executive', 'grace.adams@demo.local', 1),
    (3, 'E3003', 'Ethan', 'Cole', 'Sales Representative', 'ethan.cole@demo.local', 1),
    (3, 'E3004', 'Lily', 'Turner', 'Account Executive', 'lily.turner@demo.local', 1);
GO

INSERT INTO ref.Customer (BranchId, CustomerCode, CustomerName, CustomerType, City, Province, CreditLimit, PreferredWarehouseId)
VALUES
    (1, 'C1004', 'Harbour Medical Supply', 'Wholesale', 'Toronto', 'Ontario', 82000.00, 1),
    (1, 'C1005', 'Metro Legal Partners', 'Retail', 'Toronto', 'Ontario', 16000.00, 1),
    (1, 'C1006', 'Pearson Training Centre', 'Government', 'Brampton', 'Ontario', 68000.00, 2),
    (1, 'C1007', 'Lakefront Dental Group', 'Wholesale', 'Oakville', 'Ontario', 54000.00, 2),
    (1, 'C1008', 'Westline Fitness Clubs', 'Retail', 'Toronto', 'Ontario', 22000.00, 1),
    (1, 'C1009', 'Union Station Foods', 'Retail', 'Toronto', 'Ontario', 14000.00, 1),
    (1, 'C1010', 'Greenlight Design Studio', 'Retail', 'Markham', 'Ontario', 17500.00, 2),
    (1, 'C1011', 'Ontario Civic Archive', 'Government', 'Toronto', 'Ontario', 93000.00, 1),
    (2, 'C2003', 'Rideau Business Centre', 'Wholesale', 'Ottawa', 'Ontario', 47000.00, 3),
    (2, 'C2004', 'Kanata Learning Lab', 'Government', 'Kanata', 'Ontario', 88000.00, 3),
    (2, 'C2005', 'ByWard Media House', 'Retail', 'Ottawa', 'Ontario', 19500.00, 3),
    (2, 'C2006', 'National Survey Group', 'Wholesale', 'Ottawa', 'Ontario', 73000.00, 3),
    (2, 'C2007', 'Parliament Catering', 'Retail', 'Ottawa', 'Ontario', 12000.00, 3),
    (2, 'C2008', 'Capital Health Clinics', 'Wholesale', 'Ottawa', 'Ontario', 64000.00, 3),
    (3, 'C3004', 'Foothills Engineering', 'Wholesale', 'Calgary', 'Alberta', 98000.00, 4),
    (3, 'C3005', 'Bow River Logistics', 'Wholesale', 'Calgary', 'Alberta', 57000.00, 4),
    (3, 'C3006', 'Stampede Event Group', 'Retail', 'Calgary', 'Alberta', 26000.00, 4),
    (3, 'C3007', 'Alpine Research Centre', 'Government', 'Calgary', 'Alberta', 110000.00, 4),
    (3, 'C3008', 'Prairie Fitness Network', 'Retail', 'Airdrie', 'Alberta', 15000.00, 4),
    (3, 'C3009', 'Northern Field Services', 'Wholesale', 'Red Deer', 'Alberta', 61000.00, 4),
    (3, 'C3010', 'Rockies Public Library', 'Government', 'Calgary', 'Alberta', 72000.00, 4),
    (3, 'C3011', 'WestPeak Architects', 'Retail', 'Calgary', 'Alberta', 21000.00, 4);
GO

INSERT INTO ref.InventoryBalance (WarehouseId, ProductId, QuantityOnHand, QuantityReserved, ReorderLevel, LastCountedUtc)
VALUES
    (1, 3, 31, 5, 8, '2026-03-05'),
    (1, 6, 14, 2, 5, '2026-03-05'),
    (1, 8, 26, 4, 6, '2026-03-05'),
    (1, 11, 68, 9, 12, '2026-03-05'),
    (1, 12, 52, 6, 10, '2026-03-05'),
    (2, 2, 16, 2, 6, '2026-03-06'),
    (2, 4, 73, 7, 14, '2026-03-06'),
    (2, 5, 44, 5, 10, '2026-03-06'),
    (2, 7, 9, 1, 4, '2026-03-06'),
    (2, 8, 15, 2, 5, '2026-03-06'),
    (2, 9, 38, 4, 8, '2026-03-06'),
    (2, 10, 80, 6, 15, '2026-03-06'),
    (2, 12, 29, 3, 6, '2026-03-06'),
    (3, 1, 12, 2, 6, '2026-03-07'),
    (3, 3, 17, 2, 6, '2026-03-07'),
    (3, 5, 33, 4, 8, '2026-03-07'),
    (3, 6, 11, 1, 4, '2026-03-07'),
    (3, 7, 7, 1, 3, '2026-03-07'),
    (3, 10, 55, 5, 12, '2026-03-07'),
    (3, 11, 24, 4, 6, '2026-03-07'),
    (3, 12, 19, 2, 5, '2026-03-07'),
    (4, 2, 11, 1, 5, '2026-03-08'),
    (4, 3, 14, 2, 5, '2026-03-08'),
    (4, 4, 51, 6, 10, '2026-03-08'),
    (4, 6, 9, 1, 4, '2026-03-08'),
    (4, 8, 18, 2, 5, '2026-03-08'),
    (4, 9, 27, 3, 7, '2026-03-08'),
    (4, 11, 31, 4, 7, '2026-03-08');
GO

INSERT INTO ops.SalesOrder (OrderNumber, BranchId, CustomerId, SalesRepId, OrderStatus, OrderDate, RequiredDate, Notes)
VALUES
    ('SO-2026-0011', 1, 9, 8, 'Closed',    '2026-01-06', '2026-01-11', 'Clinic docking refresh'),
    ('SO-2026-0012', 1, 10, 9, 'Shipped',  '2026-01-09', '2026-01-15', 'Office workstation rollout'),
    ('SO-2026-0013', 1, 11, 2, 'Approved', '2026-01-22', '2026-01-29', 'Training room equipment'),
    ('SO-2026-0014', 1, 12, 3, 'Packed',   '2026-01-24', '2026-01-30', 'Dental reception upgrade'),
    ('SO-2026-0015', 1, 13, 8, 'Draft',    '2026-02-03', '2026-02-09', 'Fitness club front desk'),
    ('SO-2026-0016', 1, 14, 9, 'Submitted','2026-02-07', '2026-02-14', 'Retail POS accessories'),
    ('SO-2026-0017', 1, 15, 2, 'Closed',   '2026-02-11', '2026-02-16', 'Creative studio laptops'),
    ('SO-2026-0018', 1, 16, 3, 'Approved', '2026-02-17', '2026-02-24', 'Archive indexing stations'),
    ('SO-2026-0019', 2, 17, 10, 'Shipped', '2026-01-08', '2026-01-13', 'Business centre refresh'),
    ('SO-2026-0020', 2, 18, 11, 'Approved','2026-01-16', '2026-01-23', 'Learning lab monitors'),
    ('SO-2026-0021', 2, 19, 5, 'Draft',    '2026-01-25', '2026-01-31', 'Media editing kits'),
    ('SO-2026-0022', 2, 20, 10, 'Closed',  '2026-02-01', '2026-02-07', 'Survey field laptops'),
    ('SO-2026-0023', 2, 21, 11, 'Packed',  '2026-02-09', '2026-02-13', 'Kitchen office monitors'),
    ('SO-2026-0024', 2, 22, 5, 'Submitted','2026-02-15', '2026-02-20', 'Clinic intake stations'),
    ('SO-2026-0025', 3, 23, 12, 'Closed',  '2026-01-07', '2026-01-14', 'Engineering workstation batch'),
    ('SO-2026-0026', 3, 24, 13, 'Shipped', '2026-01-14', '2026-01-21', 'Logistics router refresh'),
    ('SO-2026-0027', 3, 25, 7, 'Approved', '2026-01-23', '2026-01-28', 'Event registration kiosks'),
    ('SO-2026-0028', 3, 26, 12, 'Draft',   '2026-02-04', '2026-02-11', 'Research analyst laptops'),
    ('SO-2026-0029', 3, 27, 13, 'Packed',  '2026-02-12', '2026-02-18', 'Fitness branch displays'),
    ('SO-2026-0030', 3, 28, 7, 'Closed',   '2026-02-18', '2026-02-24', 'Field service tablets'),
    ('SO-2026-0031', 3, 29, 12, 'Approved','2026-02-22', '2026-03-01', 'Library circulation desks'),
    ('SO-2026-0032', 3, 30, 13, 'Submitted','2026-03-01', '2026-03-06', 'Architect project stations'),
    ('SO-2026-0033', 1, 1, 8, 'Draft',     '2026-03-05', '2026-03-10', 'Supplemental laptop order'),
    ('SO-2026-0034', 2, 4, 10, 'Approved', '2026-03-06', '2026-03-11', 'Office hotspot upgrade'),
    ('SO-2026-0035', 3, 6, 12, 'Packed',   '2026-03-07', '2026-03-13', 'Energy site rugged devices'),
    ('SO-2026-0036', 1, 9, 9, 'Closed',    '2026-03-08', '2026-03-12', 'Late quarter accessories'),
    ('SO-2026-0037', 2, 20, 11, 'Closed',  '2026-03-09', '2026-03-16', 'Follow-up branch order'),
    ('SO-2026-0038', 3, 24, 13, 'Draft',   '2026-03-10', '2026-03-17', 'Router spare pool'),
    ('SO-2026-0039', 1, 16, 2, 'Submitted','2026-03-11', '2026-03-18', 'Archive workstation add-on'),
    ('SO-2026-0040', 2, 18, 5, 'Approved', '2026-03-12', '2026-03-19', 'Lab headset refresh');
GO

INSERT INTO ops.SalesOrderLine (SalesOrderId, LineNumber, ProductId, WarehouseId, Quantity, UnitPrice, DiscountPercent, LineStatus)
VALUES
    (11, 1, 9, 1, 20, 182.00, 1.00, 'Shipped'),
    (11, 2, 10, 1, 20, 76.00, 0.00, 'Shipped'),
    (12, 1, 1, 1, 8, 1160.00, 2.00, 'Shipped'),
    (12, 2, 4, 1, 16, 228.00, 1.00, 'Shipped'),
    (13, 1, 2, 1, 12, 1555.00, 2.50, 'Allocated'),
    (13, 2, 11, 1, 18, 145.00, 0.00, 'Allocated'),
    (14, 1, 5, 1, 14, 318.00, 1.50, 'Allocated'),
    (14, 2, 9, 1, 14, 183.00, 0.00, 'Allocated'),
    (15, 1, 10, 1, 12, 78.00, 0.00, 'Open'),
    (15, 2, 12, 1, 8, 97.00, 0.00, 'Open'),
    (16, 1, 4, 1, 10, 232.00, 0.00, 'Open'),
    (16, 2, 11, 1, 10, 146.00, 0.00, 'Open'),
    (17, 1, 3, 2, 9, 975.00, 2.00, 'Shipped'),
    (17, 2, 9, 2, 9, 184.00, 0.00, 'Shipped'),
    (18, 1, 1, 1, 6, 1170.00, 1.00, 'Allocated'),
    (18, 2, 4, 1, 12, 229.00, 0.00, 'Allocated'),
    (19, 1, 5, 3, 18, 319.00, 2.00, 'Shipped'),
    (19, 2, 8, 3, 6, 490.00, 0.00, 'Shipped'),
    (20, 1, 4, 3, 24, 230.00, 1.00, 'Allocated'),
    (20, 2, 10, 3, 24, 77.00, 0.00, 'Allocated'),
    (21, 1, 6, 3, 4, 625.00, 0.00, 'Open'),
    (21, 2, 11, 3, 6, 147.00, 0.00, 'Open'),
    (22, 1, 1, 3, 10, 1180.00, 2.50, 'Shipped'),
    (22, 2, 9, 3, 10, 185.00, 0.00, 'Shipped'),
    (23, 1, 5, 3, 16, 321.00, 1.00, 'Allocated'),
    (23, 2, 10, 3, 16, 78.00, 0.00, 'Allocated'),
    (24, 1, 1, 3, 7, 1185.00, 1.50, 'Open'),
    (24, 2, 4, 3, 14, 233.00, 0.00, 'Open'),
    (25, 1, 2, 4, 10, 1565.00, 2.00, 'Shipped'),
    (25, 2, 6, 4, 8, 628.00, 0.00, 'Shipped'),
    (26, 1, 8, 4, 10, 492.00, 1.00, 'Shipped'),
    (26, 2, 7, 4, 6, 880.00, 1.50, 'Shipped'),
    (27, 1, 3, 4, 8, 980.00, 0.00, 'Allocated'),
    (27, 2, 9, 4, 12, 186.00, 0.00, 'Allocated'),
    (28, 1, 1, 4, 9, 1190.00, 2.00, 'Open'),
    (28, 2, 11, 4, 9, 148.00, 0.00, 'Open'),
    (29, 1, 5, 4, 20, 322.00, 1.00, 'Allocated'),
    (29, 2, 10, 4, 20, 79.00, 0.00, 'Allocated'),
    (30, 1, 2, 4, 5, 1570.00, 1.00, 'Shipped'),
    (30, 2, 12, 4, 15, 98.00, 0.00, 'Shipped'),
    (31, 1, 4, 4, 18, 234.00, 1.00, 'Allocated'),
    (31, 2, 9, 4, 18, 187.00, 0.00, 'Allocated'),
    (32, 1, 1, 4, 6, 1195.00, 1.00, 'Open'),
    (32, 2, 5, 4, 12, 323.00, 0.00, 'Open'),
    (33, 1, 2, 1, 4, 1560.00, 1.00, 'Open'),
    (33, 2, 9, 1, 6, 183.00, 0.00, 'Open'),
    (34, 1, 8, 3, 5, 494.00, 0.00, 'Allocated'),
    (34, 2, 10, 3, 15, 78.00, 0.00, 'Allocated'),
    (35, 1, 1, 4, 3, 1200.00, 0.00, 'Allocated'),
    (35, 2, 7, 4, 3, 885.00, 0.00, 'Allocated'),
    (36, 1, 11, 1, 25, 145.00, 1.00, 'Shipped'),
    (36, 2, 12, 1, 18, 98.00, 0.00, 'Shipped'),
    (37, 1, 3, 3, 5, 985.00, 1.00, 'Shipped'),
    (37, 2, 9, 3, 10, 186.00, 0.00, 'Shipped'),
    (38, 1, 8, 4, 4, 495.00, 0.00, 'Open'),
    (38, 2, 7, 4, 2, 890.00, 0.00, 'Open'),
    (39, 1, 1, 1, 4, 1190.00, 1.00, 'Open'),
    (39, 2, 4, 1, 8, 232.00, 0.00, 'Open'),
    (40, 1, 11, 3, 30, 144.00, 1.00, 'Allocated'),
    (40, 2, 6, 3, 6, 627.00, 0.00, 'Allocated');
GO

INSERT INTO ops.Shipment (ShipmentNumber, SalesOrderId, WarehouseId, ShipmentDate, CarrierName, TrackingNumber, ShipmentStatus)
VALUES
    ('SH-2026-0004', 11, 1, '2026-01-09', 'Maple Freight', 'MF-10004', 'Delivered'),
    ('SH-2026-0005', 12, 1, '2026-01-12', 'Maple Freight', 'MF-10005', 'Delivered'),
    ('SH-2026-0006', 17, 2, '2026-02-13', 'Maple Freight', 'MF-10006', 'Delivered'),
    ('SH-2026-0007', 19, 3, '2026-01-11', 'Capital Courier', 'CC-20007', 'Delivered'),
    ('SH-2026-0008', 22, 3, '2026-02-04', 'Capital Courier', 'CC-20008', 'Delivered'),
    ('SH-2026-0009', 25, 4, '2026-01-11', 'Prairie Express', 'PE-30009', 'Delivered'),
    ('SH-2026-0010', 26, 4, '2026-01-18', 'Prairie Express', 'PE-30010', 'Delivered'),
    ('SH-2026-0011', 30, 4, '2026-02-22', 'Prairie Express', 'PE-30011', 'Delivered'),
    ('SH-2026-0012', 36, 1, '2026-03-10', 'Maple Freight', 'MF-10012', 'Shipped'),
    ('SH-2026-0013', 37, 3, '2026-03-12', 'Capital Courier', 'CC-20013', 'Shipped');
GO

INSERT INTO ops.ShipmentLine (ShipmentId, SalesOrderLineId, QuantityShipped)
VALUES
    (4, 19, 20),
    (4, 20, 20),
    (5, 21, 8),
    (5, 22, 16),
    (6, 31, 9),
    (6, 32, 9),
    (7, 35, 18),
    (7, 36, 6),
    (8, 41, 10),
    (8, 42, 10),
    (9, 47, 10),
    (9, 48, 8),
    (10, 49, 10),
    (10, 50, 6),
    (11, 57, 5),
    (11, 58, 15),
    (12, 63, 25),
    (12, 64, 18),
    (13, 65, 5),
    (13, 66, 10);
GO

INSERT INTO ops.Invoice (InvoiceNumber, SalesOrderId, InvoiceDate, InvoiceStatus, SubtotalAmount, TaxAmount, TotalAmount, DueDate)
VALUES
    ('INV-2026-0005', 11, '2026-01-10', 'Paid',          5160.00, 670.80, 5830.80, '2026-02-09'),
    ('INV-2026-0006', 12, '2026-01-13', 'Paid',         12928.00, 1680.64, 14608.64, '2026-02-12'),
    ('INV-2026-0007', 17, '2026-02-14', 'Paid',         10461.00, 1359.93, 11820.93, '2026-03-16'),
    ('INV-2026-0008', 19, '2026-01-12', 'Paid',          8682.00, 1128.66, 9810.66,  '2026-02-11'),
    ('INV-2026-0009', 22, '2026-02-05', 'Paid',         13575.00, 1764.75, 15339.75, '2026-03-07'),
    ('INV-2026-0010', 25, '2026-01-12', 'Paid',         20614.00, 2679.82, 23293.82, '2026-02-11'),
    ('INV-2026-0011', 26, '2026-01-19', 'Paid',         10204.00, 1326.52, 11530.52, '2026-02-18'),
    ('INV-2026-0012', 30, '2026-02-23', 'Paid',          9310.00, 1210.30, 10520.30, '2026-03-25'),
    ('INV-2026-0013', 34, '2026-03-07', 'Open',          3640.00, 473.20,  4113.20,  '2026-04-06'),
    ('INV-2026-0014', 35, '2026-03-08', 'Open',          6255.00, 813.15,  7068.15,  '2026-04-07'),
    ('INV-2026-0015', 36, '2026-03-11', 'PartiallyPaid', 5381.00, 699.53,  6080.53,  '2026-04-10'),
    ('INV-2026-0016', 37, '2026-03-13', 'Paid',          6785.00, 882.05,  7667.05,  '2026-04-12');
GO

INSERT INTO ops.Payment (InvoiceId, PaymentDate, PaymentMethod, Amount, ReferenceNumber)
VALUES
    (5,  '2026-01-24', 'Wire',   5830.80, 'WIRE-9015'),
    (6,  '2026-01-30', 'Card',  14608.64, 'CARD-4406'),
    (7,  '2026-02-28', 'Wire',  11820.93, 'WIRE-9017'),
    (8,  '2026-01-29', 'Cheque', 9810.66, 'CHQ-2008'),
    (9,  '2026-02-26', 'Wire',  15339.75, 'WIRE-9019'),
    (10, '2026-01-27', 'Wire',  23293.82, 'WIRE-9020'),
    (11, '2026-02-06', 'Card',  11530.52, 'CARD-4411'),
    (12, '2026-03-05', 'Wire',  10520.30, 'WIRE-9022'),
    (15, '2026-03-20', 'Wire',   3000.00, 'WIRE-9025'),
    (16, '2026-03-24', 'Card',   7667.05, 'CARD-4416');
GO

/*
Add a denser operational history so analytical prompts have meaningful shipment activity
across the last 48 full months without changing the schema.
*/
CREATE TABLE #OrderSeed
(
    GeneratedKey INT NOT NULL PRIMARY KEY,
    MonthStart   DATE NOT NULL,
    SlotNo       INT NOT NULL,
    BranchId     INT NOT NULL,
    CustomerId   INT NOT NULL,
    SalesRepId   INT NOT NULL,
    WarehouseId  INT NOT NULL,
    OrderDate    DATE NOT NULL,
    ShipmentDate DATE NOT NULL,
    OrderNumber  NVARCHAR(20) NOT NULL,
    ShipmentNumber NVARCHAR(20) NOT NULL
);

CREATE TABLE #GeneratedOrders
(
    GeneratedKey INT NOT NULL PRIMARY KEY,
    SalesOrderId INT NOT NULL,
    BranchId     INT NOT NULL,
    WarehouseId  INT NOT NULL,
    OrderDate    DATE NOT NULL,
    ShipmentDate DATE NOT NULL,
    ShipmentNumber NVARCHAR(20) NOT NULL
);

CREATE TABLE #GeneratedOrderLines
(
    GeneratedKey     INT NOT NULL,
    SalesOrderLineId INT NOT NULL,
    PRIMARY KEY (GeneratedKey, SalesOrderLineId)
);

CREATE TABLE #GeneratedShipments
(
    GeneratedKey INT NOT NULL PRIMARY KEY,
    ShipmentId   INT NOT NULL
);

;WITH MonthSeed AS
(
    SELECT
        CAST(DATEADD(MONTH, -48, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)) AS DATE) AS MonthStart,
        0 AS MonthOffset
    UNION ALL
    SELECT
        CAST(DATEADD(MONTH, 1, MonthStart) AS DATE),
        MonthOffset + 1
    FROM MonthSeed
    WHERE MonthOffset < 47
),
ShipmentSlots AS
(
    SELECT 1 AS SlotNo
    UNION ALL SELECT 2
    UNION ALL SELECT 3
),
PreparedOrders AS
(
    SELECT
        ROW_NUMBER() OVER (ORDER BY m.MonthStart, s.SlotNo) AS GeneratedKey,
        m.MonthStart,
        s.SlotNo,
        CASE s.SlotNo
            WHEN 1 THEN 1
            WHEN 2 THEN 2
            ELSE 3
        END AS BranchId
    FROM MonthSeed m
    CROSS JOIN ShipmentSlots s
)
INSERT INTO #OrderSeed
(
    GeneratedKey,
    MonthStart,
    SlotNo,
    BranchId,
    CustomerId,
    SalesRepId,
    WarehouseId,
    OrderDate,
    ShipmentDate,
    OrderNumber,
    ShipmentNumber
)
SELECT
    po.GeneratedKey,
    po.MonthStart,
    po.SlotNo,
    po.BranchId,
    CASE po.BranchId
        WHEN 1 THEN CHOOSE(((po.GeneratedKey - 1) % 11) + 1, 1, 2, 3, 9, 10, 11, 12, 13, 14, 15, 16)
        WHEN 2 THEN CHOOSE(((po.GeneratedKey - 1) % 8) + 1, 4, 5, 17, 18, 19, 20, 21, 22)
        ELSE CHOOSE(((po.GeneratedKey - 1) % 11) + 1, 6, 7, 8, 23, 24, 25, 26, 27, 28, 29, 30)
    END,
    CASE po.BranchId
        WHEN 1 THEN CHOOSE(((po.GeneratedKey - 1) % 4) + 1, 2, 3, 8, 9)
        WHEN 2 THEN CHOOSE(((po.GeneratedKey - 1) % 3) + 1, 5, 10, 11)
        ELSE CHOOSE(((po.GeneratedKey - 1) % 3) + 1, 7, 12, 13)
    END,
    CASE po.BranchId
        WHEN 1 THEN CHOOSE(((po.GeneratedKey - 1) % 2) + 1, 1, 2)
        WHEN 2 THEN 3
        ELSE 4
    END,
    DATEADD(DAY, CASE po.SlotNo WHEN 1 THEN 3 WHEN 2 THEN 11 ELSE 19 END, po.MonthStart),
    DATEADD(DAY, CASE po.SlotNo WHEN 1 THEN 5 WHEN 2 THEN 13 ELSE 21 END, po.MonthStart),
    CONCAT('SOH-', CONVERT(CHAR(6), po.MonthStart, 112), '-', RIGHT(CONCAT('0', po.SlotNo), 2)),
    CONCAT('SHH-', CONVERT(CHAR(6), po.MonthStart, 112), '-', RIGHT(CONCAT('0', po.SlotNo), 2))
FROM PreparedOrders po
OPTION (MAXRECURSION 48);

INSERT INTO ops.SalesOrder
(
    OrderNumber,
    BranchId,
    CustomerId,
    SalesRepId,
    OrderStatus,
    OrderDate,
    RequiredDate,
    Notes
)
SELECT
    os.OrderNumber,
    os.BranchId,
    os.CustomerId,
    os.SalesRepId,
    'Closed',
    os.OrderDate,
    DATEADD(DAY, 7, os.OrderDate),
    'Historical shipment seed'
FROM #OrderSeed os;

INSERT INTO #GeneratedOrders (GeneratedKey, SalesOrderId, BranchId, WarehouseId, OrderDate, ShipmentDate, ShipmentNumber)
SELECT
    os.GeneratedKey,
    so.SalesOrderId,
    os.BranchId,
    os.WarehouseId,
    os.OrderDate,
    os.ShipmentDate,
    os.ShipmentNumber
FROM #OrderSeed os
INNER JOIN ops.SalesOrder so ON so.OrderNumber = os.OrderNumber;

INSERT INTO ops.SalesOrderLine
(
    SalesOrderId,
    LineNumber,
    ProductId,
    WarehouseId,
    Quantity,
    UnitPrice,
    DiscountPercent,
    LineStatus
)
SELECT
    go.SalesOrderId,
    source.LineNumber,
    source.ProductId,
    go.WarehouseId,
    source.Quantity,
    p.UnitPrice,
    source.DiscountPercent,
    'Shipped'
FROM #GeneratedOrders go
CROSS APPLY
(
    VALUES
    (
        1,
        CHOOSE(((go.GeneratedKey - 1) % 6) + 1, 1, 2, 3, 4, 5, 6),
        4 + (go.GeneratedKey % 6),
        CAST((go.GeneratedKey % 4) * 1.00 AS DECIMAL(5,2))
    ),
    (
        2,
        CHOOSE(((go.GeneratedKey + 1) % 6) + 1, 7, 8, 9, 10, 11, 12),
        6 + (go.GeneratedKey % 5),
        CAST(((go.GeneratedKey + 1) % 3) * 1.50 AS DECIMAL(5,2))
    )
) source(LineNumber, ProductId, Quantity, DiscountPercent)
INNER JOIN ref.Product p ON p.ProductId = source.ProductId;

INSERT INTO #GeneratedOrderLines (GeneratedKey, SalesOrderLineId)
SELECT
    go.GeneratedKey,
    sol.SalesOrderLineId
FROM #GeneratedOrders go
INNER JOIN ops.SalesOrderLine sol ON sol.SalesOrderId = go.SalesOrderId;

INSERT INTO ops.Shipment
(
    ShipmentNumber,
    SalesOrderId,
    WarehouseId,
    ShipmentDate,
    CarrierName,
    TrackingNumber,
    ShipmentStatus
)
SELECT
    go.ShipmentNumber,
    go.SalesOrderId,
    go.WarehouseId,
    go.ShipmentDate,
    CASE go.BranchId
        WHEN 1 THEN 'Maple Freight'
        WHEN 2 THEN 'Capital Courier'
        ELSE 'Prairie Express'
    END,
    CONCAT(
        CASE go.BranchId
            WHEN 1 THEN 'MF'
            WHEN 2 THEN 'CC'
            ELSE 'PE'
        END,
        '-H',
        RIGHT(CONCAT('00000', go.GeneratedKey), 5)
    ),
    'Delivered'
FROM #GeneratedOrders go;

INSERT INTO #GeneratedShipments (GeneratedKey, ShipmentId)
SELECT
    go.GeneratedKey,
    sh.ShipmentId
FROM #GeneratedOrders go
INNER JOIN ops.Shipment sh ON sh.ShipmentNumber = go.ShipmentNumber;

INSERT INTO ops.ShipmentLine (ShipmentId, SalesOrderLineId, QuantityShipped)
SELECT
    gs.ShipmentId,
    gol.SalesOrderLineId,
    sol.Quantity
FROM #GeneratedShipments gs
INNER JOIN #GeneratedOrderLines gol ON gol.GeneratedKey = gs.GeneratedKey
INNER JOIN ops.SalesOrderLine sol ON sol.SalesOrderLineId = gol.SalesOrderLineId;

DROP TABLE #GeneratedShipments;
DROP TABLE #GeneratedOrderLines;
DROP TABLE #GeneratedOrders;
DROP TABLE #OrderSeed;
GO

INSERT INTO sec.AppUser (LoginName, DisplayName, EmployeeId)
VALUES
    ('oliver', 'Oliver Grant', 8),
    ('chloe', 'Chloe Bennett', 9),
    ('jacob', 'Jacob Lewis', 10),
    ('grace', 'Grace Adams', 11),
    ('ethan', 'Ethan Cole', 12),
    ('lily', 'Lily Turner', 13),
    ('ops.tor', 'Toronto Operations', NULL),
    ('ops.ott', 'Ottawa Operations', NULL),
    ('ops.cal', 'Calgary Operations', NULL);
GO

INSERT INTO sec.UserRole (AppUserId, AppRoleId)
VALUES
    (8, 1),
    (9, 1),
    (10, 1),
    (11, 1),
    (12, 1),
    (13, 1),
    (14, 4),
    (15, 4),
    (16, 4);
GO

INSERT INTO sec.UserBranchAccess (AppUserId, BranchId, CanRead, CanWrite)
VALUES
    (8, 1, 1, 1),
    (9, 1, 1, 1),
    (10, 2, 1, 1),
    (11, 2, 1, 1),
    (12, 3, 1, 1),
    (13, 3, 1, 1),
    (14, 1, 1, 0),
    (15, 2, 1, 0),
    (16, 3, 1, 0);
GO

INSERT INTO sec.UserCustomerAccess (AppUserId, CustomerId, CanRead, CanWrite)
VALUES
    (8, 9, 1, 1),
    (8, 13, 1, 1),
    (8, 15, 1, 1),
    (9, 10, 1, 1),
    (9, 12, 1, 1),
    (9, 16, 1, 1),
    (10, 17, 1, 1),
    (10, 20, 1, 1),
    (10, 22, 1, 1),
    (11, 18, 1, 1),
    (11, 19, 1, 1),
    (11, 21, 1, 1),
    (12, 23, 1, 1),
    (12, 26, 1, 1),
    (12, 29, 1, 1),
    (13, 24, 1, 1),
    (13, 25, 1, 1),
    (13, 30, 1, 1);
GO

INSERT INTO sec.UserWarehouseAccess (AppUserId, WarehouseId, CanRead, CanWrite)
VALUES
    (8, 1, 1, 0),
    (8, 2, 1, 0),
    (9, 1, 1, 0),
    (10, 3, 1, 0),
    (11, 3, 1, 0),
    (12, 4, 1, 0),
    (13, 4, 1, 0),
    (14, 1, 1, 1),
    (14, 2, 1, 1),
    (15, 3, 1, 1),
    (16, 4, 1, 1);
GO

INSERT INTO sec.AuditLog (AppUserId, EventUtc, EventType, EntityName, EntityId, Details)
VALUES
    (8,  '2026-03-05T09:00:00', 'ORDER_CREATE', 'SalesOrder', '33', 'Created supplemental order'),
    (9,  '2026-03-05T09:20:00', 'ORDER_READ', 'Customer', '16', 'Reviewed archive customer'),
    (10, '2026-03-06T10:10:00', 'ORDER_APPROVE', 'SalesOrder', '34', 'Approved hotspot upgrade'),
    (11, '2026-03-06T10:25:00', 'ORDER_READ', 'InventoryBalance', '3:11', 'Checked headset stock'),
    (12, '2026-03-07T11:05:00', 'ORDER_CREATE', 'SalesOrder', '35', 'Created energy site order'),
    (13, '2026-03-07T11:15:00', 'ORDER_READ', 'Customer', '24', 'Reviewed logistics profile'),
    (14, '2026-03-08T08:40:00', 'SHIPMENT_UPDATE', 'Shipment', '12', 'Packed March shipment'),
    (15, '2026-03-08T09:00:00', 'SHIPMENT_UPDATE', 'Shipment', '13', 'Prepared courier handoff'),
    (16, '2026-03-08T09:30:00', 'INVENTORY_READ', 'InventoryBalance', '4:7', 'Checked router availability'),
    (6,  '2026-03-09T10:00:00', 'INVOICE_READ', 'Invoice', '13', 'Reviewed March receivable'),
    (6,  '2026-03-10T10:00:00', 'INVOICE_READ', 'Invoice', '15', 'Reviewed partial payment'),
    (5,  '2026-03-10T11:00:00', 'ORDER_APPROVE', 'SalesOrder', '40', 'Approved lab headset refresh'),
    (1,  '2026-03-11T09:10:00', 'ORDER_READ', 'SalesOrder', '11', 'Checked historical order'),
    (2,  '2026-03-11T09:20:00', 'ORDER_READ', 'SalesOrder', '39', 'Reviewed pending archive add-on'),
    (3,  '2026-03-12T09:30:00', 'ORDER_CREATE', 'SalesOrder', '40', 'Prepared headset refresh'),
    (4,  '2026-03-12T09:40:00', 'ORDER_READ', 'Customer', '29', 'Looked up library account'),
    (7,  '2026-03-13T08:15:00', 'SHIPMENT_UPDATE', 'Shipment', '11', 'Marked delivered'),
    (14, '2026-03-13T08:20:00', 'SHIPMENT_UPDATE', 'Shipment', '12', 'Updated manifest'),
    (15, '2026-03-13T08:25:00', 'SHIPMENT_UPDATE', 'Shipment', '13', 'Updated dispatch queue'),
    (16, '2026-03-14T08:30:00', 'INVENTORY_READ', 'InventoryBalance', '1:1', 'Checked laptop on-hand');
GO
