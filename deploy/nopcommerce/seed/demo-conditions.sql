-- Idempotent demo conditions after nopCommerce sample data is installed.
-- No passwords, connection strings, or store secrets.
SET NOCOUNT ON;

UPDATE [Store]
SET [Url] = N'http://127.0.0.1:5088/';

IF NOT EXISTS (SELECT 1 FROM [Store] WHERE [Url] = N'http://127.0.0.1:5088/')
    THROW 50001, 'Store URL was not pinned to http://127.0.0.1:5088/.', 1;

DECLARE @productId int;
SELECT TOP (1) @productId = [Id]
FROM [Product]
WHERE [Deleted] = 0
ORDER BY [Id];

IF @productId IS NULL
    THROW 50002, 'Sample data has no product to mark low-stock.', 1;

UPDATE [Product]
SET [ManageInventoryMethodId] = 1,
    [StockQuantity] = 1,
    [MinStockQuantity] = 5,
    [LowStockActivityId] = 0,
    [NotifyAdminForQuantityBelow] = 5,
    [DisplayStockAvailability] = 1
WHERE [Id] = @productId;

DECLARE @orderId int;
SELECT TOP (1) @orderId = [Id]
FROM [Order]
WHERE [Deleted] = 0
ORDER BY [Id];

IF @orderId IS NULL
    THROW 50003, 'Sample data has no order to mark pending.', 1;

UPDATE [Order]
SET [OrderStatusId] = 10
WHERE [Id] = @orderId;
