using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 40 * 1024 * 1024;
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();

var distPath = Path.Combine(app.Environment.ContentRootPath, "dist");
var userDataPath = Path.Combine(app.Environment.ContentRootPath, "UserData");
Directory.CreateDirectory(userDataPath);
if (Directory.Exists(distPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(distPath),
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(distPath),
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(userDataPath),
    RequestPath = "/UserData",
});

var connectionString = builder.Configuration.GetConnectionString("Wms")
    ?? "Server=.;Database=hh2j1332;Integrated Security=True;TrustServerCertificate=True;Encrypt=False";

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

app.MapGet("/api/delivery-summary", async (string? loginId) =>
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT COUNT(DISTINCT SBI.BillID)
        FROM dbo.Wlt_Wms_CarLoadBillIndex CLBI
        INNER JOIN dbo.Wlt_Wms_CarLoadBillBody CLBB ON CLBB.BillID=CLBI.BillID
        INNER JOIN dbo.Wlt_Wms_SourceBillIndex SBI ON SBI.BillID=CLBB.SourceBillID
        WHERE CLBI.Deleted=0 AND SBI.Deleted=0
          AND (@LoginID='' OR CLBI.DriverID=@LoginID)
          AND ISNULL(SBI.DispatchBackState,0)=1
          AND LEFT(ISNULL(SBI.DispatchBackTime,''),10)=CONVERT(varchar(10), GETDATE(), 120)
        """;
    AddString(cmd, "@LoginID", loginId);
    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return Results.Ok(new { todayCompletedCount = count });
});

app.MapPost("/api/login", async (LoginRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "请输入司机和密码" });
    }

    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 1 LoginID, LoginCode, LoginName, Mobile, PassWord
        FROM dbo.Wlt_Wms_User
        WHERE Deleted=0 AND IsStop=0
          AND (LoginName=@Login OR LoginCode=@Login OR Mobile=@Login OR LoginID=@Login)
        """;
    cmd.Parameters.AddWithValue("@Login", request.Login.Trim());

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.Unauthorized();
    }

    var storedPassword = ReadString(reader, "PassWord");
    var encryptedInput = EncryptPassword(request.Password);
    if (!string.Equals(storedPassword, encryptedInput, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new UserDto(
        ReadString(reader, "LoginID"),
        ReadString(reader, "LoginCode"),
        ReadString(reader, "LoginName"),
        ReadString(reader, "Mobile")));
});

app.MapGet("/api/routes", async (
    string? loginId,
    string? dateFrom,
    string? dateTo,
    string? status) =>
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    var routes = await LoadConfiguredRoutes(conn);
    if (routes.Count > 0)
    {
        return Results.Ok(routes);
    }

    routes = new List<string>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT DISTINCT
          CASE WHEN ISNULL(CLBI.DeliveryAreaNames,'')='' THEN @DefaultRoute ELSE CLBI.DeliveryAreaNames END AS RouteName
        FROM dbo.Wlt_Wms_CarLoadBillIndex CLBI
        INNER JOIN dbo.Wlt_Wms_CarLoadBillBody CLBB ON CLBB.BillID=CLBI.BillID
        INNER JOIN dbo.Wlt_Wms_SourceBillIndex SBI ON SBI.BillID=CLBB.SourceBillID
        WHERE CLBI.Deleted=0 AND SBI.Deleted=0
          AND (@LoginID='' OR CLBI.DriverID=@LoginID)
          AND (@DateFrom='' OR CLBI.BillDate>=@DateFrom)
          AND (@DateTo='' OR CLBI.BillDate<=@DateTo)
          AND (
            @Status='all'
            OR (@Status='undelivered' AND ISNULL(SBI.DispatchBackState,0)=0)
            OR @Status='completed' AND ISNULL(SBI.DispatchBackState,0)=1
          )
        ORDER BY RouteName
        """;
    AddString(cmd, "@LoginID", loginId);
    AddString(cmd, "@DateFrom", dateFrom);
    AddString(cmd, "@DateTo", dateTo);
    AddString(cmd, "@Status", string.IsNullOrWhiteSpace(status) ? "undelivered" : status);
    AddString(cmd, "@DefaultRoute", "默认线路");

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var routeName = ReadString(reader, "RouteName");
        if (!string.IsNullOrWhiteSpace(routeName))
        {
            routes.Add(routeName);
        }
    }
    return Results.Ok(routes);
});

app.MapGet("/api/deliveries", async (
    string? loginId,
    string? q,
    string? route,
    string? dateFrom,
    string? dateTo,
    string? status) =>
{
    var rows = new List<DeliveryDto>();
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT
          CLBI.BillCode AS CarLoadBillCode,
          CLBI.BillID AS CarLoadBillID,
          CLBI.BillDate,
          CLBI.CarName,
          CLBI.DriverID,
          U.LoginName AS DriverName,
          U.Mobile AS DriverMobile,
          SBI.BillCode AS SourceBillCode,
          SBI.BillID AS SourceBillID,
          SBI.BTypeID,
          SBI.BTypeCode,
          SBI.BTypeName AS CustomerName,
          SBI.DispatchState,
          SBI.DispatchBackState,
          SBI.DispatchBackTime,
          CLBB.SortNum,
          COALESCE(CAST(Distance.Mileage AS DECIMAL(18,4)), CLBB.Mileage) AS Mileage,
          Coord.CustomerLongitude,
          Coord.CustomerLatitude,
          CASE
            WHEN ISNULL(RouteConfig.ExpressName,'')<>'' THEN RouteConfig.ExpressName
            WHEN ISNULL(CLBI.DeliveryAreaNames,'')<>'' THEN CLBI.DeliveryAreaNames
            ELSE @DefaultRoute
          END AS RouteName,
          B.Person,
          B.linker,
          B.linkerTel,
          B.moPhone,
          B.TelAndAddress,
          B.province,
          B.city,
          B.region,
          B.Area
        FROM dbo.Wlt_Wms_CarLoadBillIndex CLBI
        INNER JOIN dbo.Wlt_Wms_CarLoadBillBody CLBB ON CLBB.BillID=CLBI.BillID
        INNER JOIN dbo.Wlt_Wms_SourceBillIndex SBI ON SBI.BillID=CLBB.SourceBillID
        LEFT JOIN dbo.Wlt_Wms_User U ON U.LoginID=CLBI.DriverID
        LEFT JOIN dbo.Btype B ON B.btypeid=SBI.BTypeID
        LEFT JOIN dbo.Wlt_Wms_BTypeExpandInfo BEI ON BEI.BTypeRecID=B.brec
        OUTER APPLY (
          SELECT TOP 1 DAEI.ExpressName
          FROM dbo.Wlt_Wms_DeliveryAreaExpressInfo DAEI
          LEFT JOIN dbo.Wlt_Wms_DeliveryAreaExpressBTypeInfo DAEB
            ON DAEB.ExpressID=DAEI.ExpressID
           AND DAEB.BTypeID=SBI.BTypeID
           AND ISNULL(DAEB.Deleted,0)=0
           AND ISNULL(DAEB.IsStop,0)=0
          WHERE ISNULL(DAEI.Deleted,0)=0
            AND ISNULL(DAEI.IsStop,0)=0
            AND (
              DAEB.SysID IS NOT NULL
              OR DAEI.ExpressName=CLBI.DeliveryAreaNames
              OR DAEI.DeliveryAreaID=CLBI.DeliveryAreaIDs
              OR CHARINDEX(',' + DAEI.DeliveryAreaID + ',', ',' + ISNULL(CLBI.DeliveryAreaIDs,'') + ',') > 0
            )
          ORDER BY CASE WHEN DAEB.SysID IS NOT NULL THEN 0 ELSE 1 END, DAEI.SortNum ASC, DAEI.SysID ASC
        ) RouteConfig
        OUTER APPLY (
          SELECT TOP 1 Longitude, Latitude
          FROM dbo.Wlt_Wms_STock ST
          WHERE ISNULL(ST.Deleted,0)=0
            AND (
              ST.KTypeID=COALESCE(NULLIF(SBI.KTypeIDMain,''), NULLIF(CLBI.KTypeID,''))
              OR ST.STypeID=COALESCE(NULLIF(CLBI.STypeID,''), NULLIF(SBI.STypeID,''))
            )
          ORDER BY CASE WHEN ST.KTypeID=COALESCE(NULLIF(SBI.KTypeIDMain,''), NULLIF(CLBI.KTypeID,'')) THEN 0 ELSE 1 END
        ) ST
        OUTER APPLY (
          SELECT
            CASE WHEN ISNUMERIC(BEI.Longitude)=1 THEN CONVERT(FLOAT, BEI.Longitude) END AS CustomerLongitude,
            CASE WHEN ISNUMERIC(BEI.Latitude)=1 THEN CONVERT(FLOAT, BEI.Latitude) END AS CustomerLatitude,
            CASE WHEN ISNUMERIC(ST.Longitude)=1 THEN CONVERT(FLOAT, ST.Longitude) END AS StockLongitude,
            CASE WHEN ISNUMERIC(ST.Latitude)=1 THEN CONVERT(FLOAT, ST.Latitude) END AS StockLatitude
        ) Coord
        OUTER APPLY (
          SELECT
            CASE
              WHEN Coord.CustomerLongitude BETWEEN -180 AND 180
               AND Coord.CustomerLatitude BETWEEN -90 AND 90
               AND Coord.StockLongitude BETWEEN -180 AND 180
               AND Coord.StockLatitude BETWEEN -90 AND 90
              THEN ROUND(dbo.FN_Wlt_Wms_GetDistance(Coord.StockLongitude, Coord.StockLatitude, Coord.CustomerLongitude, Coord.CustomerLatitude) / 1000.0, 2)
            END AS Mileage
        ) Distance
        WHERE CLBI.Deleted=0 AND SBI.Deleted=0
          AND (@LoginID='' OR CLBI.DriverID=@LoginID)
          AND (@Q='' OR SBI.BillCode LIKE @QLike OR SBI.BTypeName LIKE @QLike)
          AND (
            @Route=''
            OR @Route=@AllRoute
            OR CLBI.DeliveryAreaNames=@Route
            OR RouteConfig.ExpressName=@Route
            OR (@Route=@DefaultRoute AND ISNULL(CLBI.DeliveryAreaNames,'')='' AND ISNULL(RouteConfig.ExpressName,'')='')
          )
          AND (@DateFrom='' OR CLBI.BillDate>=@DateFrom)
          AND (@DateTo='' OR CLBI.BillDate<=@DateTo)
          AND (
            @Status='all'
            OR (@Status='undelivered' AND ISNULL(SBI.DispatchBackState,0)=0)
            OR (@Status='completed' AND ISNULL(SBI.DispatchBackState,0)=1)
          )
        ORDER BY Mileage ASC, CLBB.SortNum ASC, SBI.BillCode ASC
        """;
    AddString(cmd, "@LoginID", loginId);
    AddString(cmd, "@Q", q);
    AddString(cmd, "@QLike", string.IsNullOrWhiteSpace(q) ? "" : $"%{q.Trim()}%");
    AddString(cmd, "@Route", route);
    AddString(cmd, "@AllRoute", "全部");
    AddString(cmd, "@DefaultRoute", "默认线路");
    AddString(cmd, "@DateFrom", dateFrom);
    AddString(cmd, "@DateTo", dateTo);
    AddString(cmd, "@Status", string.IsNullOrWhiteSpace(status) ? "undelivered" : status);

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(ReadDelivery(reader));
    }
    return Results.Ok(rows);
});

app.MapGet("/api/deliveries/{sourceBillId}", async (string sourceBillId) =>
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    var delivery = await LoadDelivery(conn, sourceBillId);
    if (delivery is null)
    {
        return Results.NotFound(new { message = "配送单不存在" });
    }

    var products = new List<ProductDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT
          wb.RowNum,
          wb.PTypeID,
          p.PTypeCode,
          p.PTypeName,
          COALESCE(NULLIF(p.BarCode,''), NULLIF(bc.BarCode,''), '') AS BarCode,
          p.Standard,
          p.Type,
          wb.UnitName,
          wb.UnitRate,
          wb.BaseQty - wb.PickGiveQty AS Quantity,
          BigUnit.UnitName AS BigUnitName,
          BigUnit.UnitRate AS BigUnitRate
        FROM dbo.Wlt_Wms_BillIndex bi
        INNER JOIN dbo.Wlt_Wms_BillBody wb ON wb.BillID=bi.BillID
        LEFT JOIN dbo.Wlt_Wms_PType p ON p.PTypeID=wb.PTypeID
        LEFT JOIN dbo.Wlt_Wms_PTypeBarCode bc ON bc.PTypeID=wb.PTypeID AND ISNULL(bc.IsDefault,0)=1
        OUTER APPLY (
          SELECT TOP 1 UnitName, UnitRate
          FROM dbo.Wlt_Wms_PTypeUnit u
          WHERE u.PTypeID=wb.PTypeID
            AND ISNULL(u.BigUnit,0)=1
            AND ISNULL(u.UnitRate,0)>ISNULL(wb.UnitRate,0)
          ORDER BY u.UnitRate DESC
        ) BigUnit
        WHERE bi.Deleted=0
          AND bi.PickType<>0
          AND bi.SourceBillCode LIKE @SourceBillCodeLike
          AND wb.BaseQty - wb.PickGiveQty > 0
        ORDER BY wb.RowNum
        """;
    AddString(cmd, "@SourceBillCodeLike", $"{delivery.BillCode}-%");
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        AddProduct(products, reader);
    }

    if (products.Count == 0)
    {
        await using var fallbackCmd = conn.CreateCommand();
        fallbackCmd.CommandText = """
            SELECT
              b.RowNum,
              b.PTypeID,
              b.PTypeCode,
              b.PTypeName,
              COALESCE(NULLIF(p.BarCode,''), NULLIF(bc.BarCode,''), '') AS BarCode,
              p.Standard,
              p.Type,
              b.UnitName,
              b.UnitRate,
              CASE WHEN b.UnitQty=0 THEN b.BaseQty ELSE b.UnitQty END AS Quantity,
              BigUnit.UnitName AS BigUnitName,
              BigUnit.UnitRate AS BigUnitRate
            FROM dbo.Wlt_Wms_SourceBillBody b
            LEFT JOIN dbo.Wlt_Wms_PType p ON p.PTypeID=b.PTypeID
            LEFT JOIN dbo.Wlt_Wms_PTypeBarCode bc ON bc.PTypeID=b.PTypeID AND ISNULL(bc.IsDefault,0)=1
            OUTER APPLY (
              SELECT TOP 1 UnitName, UnitRate
              FROM dbo.Wlt_Wms_PTypeUnit u
              WHERE u.PTypeID=b.PTypeID
                AND ISNULL(u.BigUnit,0)=1
                AND ISNULL(u.UnitRate,0)>ISNULL(b.UnitRate,0)
              ORDER BY u.UnitRate DESC
            ) BigUnit
            WHERE b.BillID=@SourceBillID
            ORDER BY b.RowNum
            """;
        AddString(fallbackCmd, "@SourceBillID", sourceBillId);
        await using var fallbackReader = await fallbackCmd.ExecuteReaderAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await fallbackReader.ReadAsync())
        {
            var name = ReadString(fallbackReader, "PTypeName");
            var barcode = ReadString(fallbackReader, "BarCode");
            var key = $"{ReadString(fallbackReader, "PTypeID")}|{name}|{barcode}";
            if (!seen.Add(key) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            AddProduct(products, fallbackReader);
        }
    }

    return Results.Ok(delivery with { Products = products });
});

app.MapPost("/api/deliveries/{sourceBillId}/complete", async (string sourceBillId, HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "请使用表单上传照片和签字" });
    }

    var form = await request.ReadFormAsync();
    var loginId = form["loginId"].ToString();
    if (string.IsNullOrWhiteSpace(loginId))
    {
        return Results.BadRequest(new { message = "缺少登录用户" });
    }

    var savedImages = new List<AccessoryImage>();

    foreach (var file in form.Files.Where(f => f.Name == "photos"))
    {
        if (file.Length == 0) continue;
        var fileName = SafeAccessoryName(file.FileName, "delivery.jpg");
        savedImages.Add(new AccessoryImage(EncodeAccessoryBytes(await ReadFileBytes(file)), fileName, "配送照片"));
    }

    var signature = form["signature"].ToString();
    if (!string.IsNullOrWhiteSpace(signature))
    {
        var signatureBytes = DecodeDataUrl(signature);
        savedImages.Add(new AccessoryImage(EncodeAccessoryBytes(signatureBytes), $"signature-{Guid.NewGuid():N}{DataUrlExtension(signature)}", "客户签字"));
    }

    if (savedImages.Count == 0)
    {
        return Results.BadRequest(new { message = "请上传照片或客户签字" });
    }

    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();
    try
    {
        int moudleNo;
        int moudleType;
        string carLoadBillId;
        string carrierId;
        string wayBillCode;
        await using (var billCmd = conn.CreateCommand())
        {
            billCmd.Transaction = (SqlTransaction)tx;
            billCmd.CommandText = """
                SELECT TOP 1
                    SourceBillID,
                    SourceBillTypeID,
                    ISNULL(DispatchBackState,0) AS DispatchBackState,
                    ISNULL(CarLoadBillID,'') AS CarLoadBillID,
                    ISNULL(CarrierID,'') AS CarrierID,
                    ISNULL(WayBillCode,'') AS WayBillCode
                FROM dbo.Wlt_Wms_SourceBillIndex
                WHERE BillID=@SourceBillID AND Deleted=0
                """;
            AddString(billCmd, "@SourceBillID", sourceBillId);
            await using var billReader = await billCmd.ExecuteReaderAsync();
            if (!await billReader.ReadAsync())
            {
                await tx.RollbackAsync();
                return Results.NotFound(new { message = "配送单不存在" });
            }

            moudleNo = Convert.ToInt32(billReader.GetValue(0), CultureInfo.InvariantCulture);
            moudleType = Convert.ToInt32(billReader.GetValue(1), CultureInfo.InvariantCulture);
            if (Convert.ToInt32(billReader["DispatchBackState"], CultureInfo.InvariantCulture) == 1)
            {
                await tx.RollbackAsync();
                return Results.BadRequest(new { message = "该单据已配送完成，不能重复操作" });
            }
            carLoadBillId = Convert.ToString(billReader["CarLoadBillID"], CultureInfo.InvariantCulture) ?? "";
            carrierId = Convert.ToString(billReader["CarrierID"], CultureInfo.InvariantCulture) ?? "";
            wayBillCode = Convert.ToString(billReader["WayBillCode"], CultureInfo.InvariantCulture) ?? "";
        }

        var accessoryGuid = Guid.NewGuid();
        var now = DateTime.Now;
        foreach (var image in savedImages)
        {
            await using var imgCmd = conn.CreateCommand();
            imgCmd.Transaction = (SqlTransaction)tx;
            imgCmd.CommandText = """
                INSERT INTO dbo.Xw_Accessory
                    (GUID, MoudleNo, MoudleType, Accessory, Name, Comment, CreateDate, LastModifyDate, ClassName)
                VALUES
                    (@GUID, @MoudleNo, @MoudleType, @Accessory, @Name, @Comment, @Now, @Now, '')
                """;
            imgCmd.Parameters.Add("@GUID", SqlDbType.UniqueIdentifier).Value = accessoryGuid;
            imgCmd.Parameters.Add("@MoudleNo", SqlDbType.Int).Value = moudleNo;
            imgCmd.Parameters.Add("@MoudleType", SqlDbType.Int).Value = moudleType;
            imgCmd.Parameters.Add("@Accessory", SqlDbType.Image).Value = image.Bytes;
            AddString(imgCmd, "@Name", image.Name);
            AddString(imgCmd, "@Comment", image.Comment);
            imgCmd.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
            await imgCmd.ExecuteNonQueryAsync();
        }

        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = (SqlTransaction)tx;
        updateCmd.CommandText = """
            UPDATE dbo.Wlt_Wms_SourceBillIndex
            SET AllPickBillCarryState=1,
                DispatchBackState=1,
                DispatchState=1,
                DispatchBackLoginID=@LoginID,
                DispatchBackTime=@Now,
                CarrierID=@CarrierID,
                WayBillCode=@WayBillCode
            WHERE BillID=@SourceBillID AND Deleted=0
            """;
        var completedAt = DateTime.Now;
        var completedAtText = completedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        AddString(updateCmd, "@LoginID", loginId);
        AddString(updateCmd, "@Now", completedAtText);
        AddString(updateCmd, "@CarrierID", carrierId);
        AddString(updateCmd, "@WayBillCode", wayBillCode);
        AddString(updateCmd, "@SourceBillID", sourceBillId);
        var affected = await updateCmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            await tx.RollbackAsync();
            return Results.NotFound(new { message = "配送单不存在" });
        }

        await using (var carLoadCmd = conn.CreateCommand())
        {
            carLoadCmd.Transaction = (SqlTransaction)tx;
            carLoadCmd.CommandText = """
                UPDATE CLBI
                SET DispatchStatus=2,
                    DispatchDate=@Now,
                    DispatchBackUseMinute=CASE WHEN ISDATE(CLBI.CreateTime)=1 THEN DATEDIFF(MINUTE, CLBI.CreateTime, @Now) ELSE 0 END
                FROM dbo.Wlt_Wms_CarLoadBillIndex CLBI
                WHERE CLBI.BillID=@CarLoadBillID
                  AND NOT EXISTS (
                      SELECT 1
                      FROM dbo.Wlt_Wms_SourceBillIndex SBI
                      WHERE SBI.CarLoadBillID=@CarLoadBillID
                        AND SBI.Deleted=0
                        AND ISNULL(SBI.DispatchBackState,0)=0
                  )
                """;
            AddString(carLoadCmd, "@Now", completedAtText);
            AddString(carLoadCmd, "@CarLoadBillID", carLoadBillId);
            await carLoadCmd.ExecuteNonQueryAsync();
        }

        var loginName = await GetLoginName(conn, (SqlTransaction)tx, loginId);
        var writeBackMessage = $"【配送完成】{loginName}；{completedAt:MM-dd HH:mm}自配 ";
        await ExecuteCheckedProcedure(
            conn,
            (SqlTransaction)tx,
            """
            DECLARE @ErrorMsg VARCHAR(2000), @ReturnValue INT;
            EXEC @ReturnValue=dbo.PR_Wlt_Wms_PickBillWriteGraspBillField
                @DataMode=@DataMode,
                @WmsBillID=@WmsBillID,
                @CancelReason=@CancelReason,
                @ErrorMsg=@ErrorMsg OUTPUT;
            SELECT @ReturnValue AS ReturnValue, @ErrorMsg AS ErrorMsg;
            """,
            command =>
            {
                AddString(command, "@DataMode", "DispatchBack");
                AddString(command, "@WmsBillID", sourceBillId);
                AddString(command, "@CancelReason", writeBackMessage);
            });

        await ExecuteCheckedProcedure(
            conn,
            (SqlTransaction)tx,
            """
            DECLARE @ErrorMsg VARCHAR(8000), @ReturnValue INT;
            EXEC @ReturnValue=dbo.PR_Wlt_Wms_PerformanceCommission
                @BillID=@BillID,
                @BillType=@BillType,
                @ErrorMsg=@ErrorMsg OUTPUT;
            SELECT @ReturnValue AS ReturnValue, @ErrorMsg AS ErrorMsg;
            """,
            command =>
            {
                AddString(command, "@BillID", sourceBillId);
                AddString(command, "@BillType", "DeliveryBill");
            });

        await tx.CommitAsync();
        return Results.Ok(new { ok = true, imageCount = savedImages.Count });
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
});

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var index = Path.Combine(distPath, "index.html");
    if (File.Exists(index))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
        return;
    }
    context.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.Run();

static async Task<List<string>> LoadConfiguredRoutes(SqlConnection conn)
{
    var routeColumnCandidates = new[]
    {
        "ExpressName",
        "DeliveryAreaName",
        "DeliveryAreaNames",
        "DeliveryArea",
        "AreaName",
        "AreaNames",
        "RouteName",
        "Name",
    };

    await using var columnCmd = conn.CreateCommand();
    columnCmd.CommandText = """
        SELECT c.name
        FROM sys.columns c
        WHERE c.object_id=OBJECT_ID(N'dbo.Wlt_Wms_DeliveryAreaExpressInfo')
          AND c.name IN (
            N'DeliveryAreaName',
            N'DeliveryAreaNames',
            N'DeliveryArea',
            N'AreaName',
            N'AreaNames',
            N'RouteName',
            N'ExpressName',
            N'Name'
          )
        """;

    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var reader = await columnCmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            existingColumns.Add(reader.GetString(0));
        }
    }

    var routeColumn = routeColumnCandidates.FirstOrDefault(existingColumns.Contains);
    if (routeColumn is null)
    {
        return [];
    }

    var filters = new List<string>();
    if (await ColumnExists(conn, "Wlt_Wms_DeliveryAreaExpressInfo", "Deleted"))
    {
        filters.Add("ISNULL(Deleted,0)=0");
    }
    if (await ColumnExists(conn, "Wlt_Wms_DeliveryAreaExpressInfo", "IsStop"))
    {
        filters.Add("ISNULL(IsStop,0)=0");
    }
    if (await ColumnExists(conn, "Wlt_Wms_DeliveryAreaExpressInfo", "Stopped"))
    {
        filters.Add("ISNULL(Stopped,0)=0");
    }

    var quotedColumn = $"[{routeColumn.Replace("]", "]]")}]";
    await using var routesCmd = conn.CreateCommand();
    routesCmd.CommandText = $"""
        SELECT DISTINCT LTRIM(RTRIM(CONVERT(NVARCHAR(200), {quotedColumn}))) AS RouteName
        FROM dbo.Wlt_Wms_DeliveryAreaExpressInfo
        {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "")}
        ORDER BY RouteName
        """;

    var routes = new List<string>();
    await using var routeReader = await routesCmd.ExecuteReaderAsync();
    while (await routeReader.ReadAsync())
    {
        var routeName = ReadString(routeReader, "RouteName");
        if (!string.IsNullOrWhiteSpace(routeName))
        {
            routes.Add(routeName);
        }
    }
    return routes;
}

static async Task<bool> ColumnExists(SqlConnection conn, string tableName, string columnName)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(@TableName)
          AND name=@ColumnName
        """;
    AddString(cmd, "@TableName", $"dbo.{tableName}");
    AddString(cmd, "@ColumnName", columnName);
    var value = await cmd.ExecuteScalarAsync();
    return value is not null;
}

static async Task<DeliveryDto?> LoadDelivery(SqlConnection conn, string sourceBillId)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT TOP 1
          CLBI.BillCode AS CarLoadBillCode,
          CLBI.BillID AS CarLoadBillID,
          CLBI.BillDate,
          CLBI.CarName,
          CLBI.DriverID,
          U.LoginName AS DriverName,
          U.Mobile AS DriverMobile,
          SBI.BillCode AS SourceBillCode,
          SBI.BillID AS SourceBillID,
          SBI.BTypeID,
          SBI.BTypeCode,
          SBI.BTypeName AS CustomerName,
          SBI.DispatchState,
          SBI.DispatchBackState,
          SBI.DispatchBackTime,
          CLBB.SortNum,
          COALESCE(CAST(Distance.Mileage AS DECIMAL(18,4)), CLBB.Mileage) AS Mileage,
          Coord.CustomerLongitude,
          Coord.CustomerLatitude,
          CASE
            WHEN ISNULL(RouteConfig.ExpressName,'')<>'' THEN RouteConfig.ExpressName
            WHEN ISNULL(CLBI.DeliveryAreaNames,'')<>'' THEN CLBI.DeliveryAreaNames
            ELSE @DefaultRoute
          END AS RouteName,
          B.Person,
          B.linker,
          B.linkerTel,
          B.moPhone,
          B.TelAndAddress,
          B.province,
          B.city,
          B.region,
          B.Area
        FROM dbo.Wlt_Wms_CarLoadBillIndex CLBI
        INNER JOIN dbo.Wlt_Wms_CarLoadBillBody CLBB ON CLBB.BillID=CLBI.BillID
        INNER JOIN dbo.Wlt_Wms_SourceBillIndex SBI ON SBI.BillID=CLBB.SourceBillID
        LEFT JOIN dbo.Wlt_Wms_User U ON U.LoginID=CLBI.DriverID
        LEFT JOIN dbo.Btype B ON B.btypeid=SBI.BTypeID
        LEFT JOIN dbo.Wlt_Wms_BTypeExpandInfo BEI ON BEI.BTypeRecID=B.brec
        OUTER APPLY (
          SELECT TOP 1 DAEI.ExpressName
          FROM dbo.Wlt_Wms_DeliveryAreaExpressInfo DAEI
          LEFT JOIN dbo.Wlt_Wms_DeliveryAreaExpressBTypeInfo DAEB
            ON DAEB.ExpressID=DAEI.ExpressID
           AND DAEB.BTypeID=SBI.BTypeID
           AND ISNULL(DAEB.Deleted,0)=0
           AND ISNULL(DAEB.IsStop,0)=0
          WHERE ISNULL(DAEI.Deleted,0)=0
            AND ISNULL(DAEI.IsStop,0)=0
            AND (
              DAEB.SysID IS NOT NULL
              OR DAEI.ExpressName=CLBI.DeliveryAreaNames
              OR DAEI.DeliveryAreaID=CLBI.DeliveryAreaIDs
              OR CHARINDEX(',' + DAEI.DeliveryAreaID + ',', ',' + ISNULL(CLBI.DeliveryAreaIDs,'') + ',') > 0
            )
          ORDER BY CASE WHEN DAEB.SysID IS NOT NULL THEN 0 ELSE 1 END, DAEI.SortNum ASC, DAEI.SysID ASC
        ) RouteConfig
        OUTER APPLY (
          SELECT TOP 1 Longitude, Latitude
          FROM dbo.Wlt_Wms_STock ST
          WHERE ISNULL(ST.Deleted,0)=0
            AND (
              ST.KTypeID=COALESCE(NULLIF(SBI.KTypeIDMain,''), NULLIF(CLBI.KTypeID,''))
              OR ST.STypeID=COALESCE(NULLIF(CLBI.STypeID,''), NULLIF(SBI.STypeID,''))
            )
          ORDER BY CASE WHEN ST.KTypeID=COALESCE(NULLIF(SBI.KTypeIDMain,''), NULLIF(CLBI.KTypeID,'')) THEN 0 ELSE 1 END
        ) ST
        OUTER APPLY (
          SELECT
            CASE WHEN ISNUMERIC(BEI.Longitude)=1 THEN CONVERT(FLOAT, BEI.Longitude) END AS CustomerLongitude,
            CASE WHEN ISNUMERIC(BEI.Latitude)=1 THEN CONVERT(FLOAT, BEI.Latitude) END AS CustomerLatitude,
            CASE WHEN ISNUMERIC(ST.Longitude)=1 THEN CONVERT(FLOAT, ST.Longitude) END AS StockLongitude,
            CASE WHEN ISNUMERIC(ST.Latitude)=1 THEN CONVERT(FLOAT, ST.Latitude) END AS StockLatitude
        ) Coord
        OUTER APPLY (
          SELECT
            CASE
              WHEN Coord.CustomerLongitude BETWEEN -180 AND 180
               AND Coord.CustomerLatitude BETWEEN -90 AND 90
               AND Coord.StockLongitude BETWEEN -180 AND 180
               AND Coord.StockLatitude BETWEEN -90 AND 90
              THEN ROUND(dbo.FN_Wlt_Wms_GetDistance(Coord.StockLongitude, Coord.StockLatitude, Coord.CustomerLongitude, Coord.CustomerLatitude) / 1000.0, 2)
            END AS Mileage
        ) Distance
        WHERE CLBI.Deleted=0 AND SBI.Deleted=0 AND SBI.BillID=@SourceBillID
        """;
    AddString(cmd, "@SourceBillID", sourceBillId);
    AddString(cmd, "@DefaultRoute", "默认线路");
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadDelivery(reader) : null;
}

static DeliveryDto ReadDelivery(SqlDataReader reader)
{
    var backState = ReadInt(reader, "DispatchBackState");
    var contact = FirstNonEmpty(ReadString(reader, "linker"), ReadString(reader, "Person"));
    var phone = FirstNonEmpty(ReadString(reader, "linkerTel"), ReadString(reader, "moPhone"), ReadString(reader, "DriverMobile"));
    var address = BuildAddress(
        ReadString(reader, "province"),
        ReadString(reader, "city"),
        ReadString(reader, "region"),
        ReadString(reader, "Area"),
        ReadString(reader, "TelAndAddress"));

    return new DeliveryDto(
        ReadString(reader, "SourceBillID"),
        ReadString(reader, "CarLoadBillCode"),
        ReadString(reader, "CarLoadBillID"),
        ReadString(reader, "SourceBillCode"),
        ReadString(reader, "BillDate"),
        ReadString(reader, "CustomerName"),
        contact,
        phone,
        address,
        ReadString(reader, "RouteName"),
        ReadDecimal(reader, "Mileage"),
        ReadNullableDecimal(reader, "CustomerLongitude"),
        ReadNullableDecimal(reader, "CustomerLatitude"),
        ReadString(reader, "CarName"),
        ReadString(reader, "DriverName"),
        ReadString(reader, "DriverID"),
        ReadInt(reader, "DispatchState"),
        backState,
        backState == 1 ? "completed" : "undelivered",
        ReadInt(reader, "SortNum"),
        ReadString(reader, "DispatchBackTime"),
        []);
}

static void AddProduct(List<ProductDto> products, SqlDataReader reader)
{
    var name = ReadString(reader, "PTypeName");
    if (string.IsNullOrWhiteSpace(name))
    {
        return;
    }

    var unitName = ReadString(reader, "UnitName");
    var quantity = ReadDecimal(reader, "Quantity");
    products.Add(new ProductDto(
        ReadString(reader, "PTypeID"),
        name,
        ReadString(reader, "BarCode"),
        ReadString(reader, "Standard"),
        ReadString(reader, "Type"),
        unitName,
        quantity,
        FormatAuxiliaryQuantity(
            quantity,
            unitName,
            ReadDecimal(reader, "UnitRate"),
            ReadString(reader, "BigUnitName"),
            ReadDecimal(reader, "BigUnitRate"))));
}

static string FormatAuxiliaryQuantity(decimal quantity, string unitName, decimal unitRate, string bigUnitName, decimal bigUnitRate)
{
    if (quantity <= 0)
    {
        return $"0{unitName}";
    }

    var baseQty = unitRate > 0 ? quantity * unitRate : quantity;
    if (string.IsNullOrWhiteSpace(bigUnitName) || bigUnitRate <= 0 || bigUnitRate <= unitRate)
    {
        return $"{FormatQty(quantity)}{unitName}";
    }

    var bigQty = decimal.Floor(baseQty / bigUnitRate);
    var smallBaseQty = baseQty - bigQty * bigUnitRate;
    var smallQty = unitRate > 0 ? smallBaseQty / unitRate : smallBaseQty;
    var parts = new List<string>();
    if (bigQty > 0)
    {
        parts.Add($"{FormatQty(bigQty)}{bigUnitName}");
    }
    if (smallQty > 0)
    {
        parts.Add($"{FormatQty(smallQty)}{unitName}");
    }
    return parts.Count > 0 ? string.Join("", parts) : $"0{unitName}";
}

static string FormatQty(decimal value)
{
    return decimal.Truncate(value) == value
        ? value.ToString("0", CultureInfo.InvariantCulture)
        : value.ToString("0.##", CultureInfo.InvariantCulture);
}

static string EncryptPassword(string plainText)
{
    using var aes = Aes.Create();
    aes.Key = Encoding.UTF8.GetBytes("zmkjpwd86982118a");
    aes.IV = Encoding.UTF8.GetBytes("4570125797502478");
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;
    var bytes = Encoding.UTF8.GetBytes(plainText);
    using var encryptor = aes.CreateEncryptor();
    return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
}

static async Task<string> GetLoginName(SqlConnection conn, SqlTransaction tx, string loginId)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        SELECT TOP 1 LoginName
        FROM dbo.Wlt_Wms_User
        WHERE LoginID=@LoginID
        """;
    AddString(cmd, "@LoginID", loginId);
    var value = Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return string.IsNullOrWhiteSpace(value) ? loginId : value.Trim();
}

static async Task ExecuteCheckedProcedure(
    SqlConnection conn,
    SqlTransaction tx,
    string commandText,
    Action<SqlCommand> configure)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = commandText;
    configure(cmd);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("ERP存储过程没有返回执行结果");
    }

    var returnValue = Convert.ToInt32(reader["ReturnValue"], CultureInfo.InvariantCulture);
    var errorMsg = Convert.ToString(reader["ErrorMsg"], CultureInfo.InvariantCulture) ?? "";
    if (returnValue == 0)
    {
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMsg) ? "ERP存储过程执行失败" : errorMsg);
    }
}

static byte[] DecodeDataUrl(string dataUrl)
{
    var comma = dataUrl.IndexOf(',');
    var base64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
    return Convert.FromBase64String(base64);
}

static string DataUrlExtension(string dataUrl)
{
    if (dataUrl.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
    if (dataUrl.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase)) return ".png";
    if (dataUrl.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
    return ".png";
}

static async Task<byte[]> ReadFileBytes(IFormFile file)
{
    await using var memory = new MemoryStream();
    await file.CopyToAsync(memory);
    return memory.ToArray();
}

static byte[] EncodeAccessoryBytes(byte[] fileBytes)
{
    using var packed = new MemoryStream();
    packed.Write(BitConverter.GetBytes(fileBytes.Length));
    using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
    {
        zlib.Write(fileBytes, 0, fileBytes.Length);
    }

    return packed.ToArray();
}

static string SafeAccessoryName(string? fileName, string fallback)
{
    var safe = Path.GetFileName(fileName);
    if (string.IsNullOrWhiteSpace(safe))
    {
        safe = fallback;
    }

    if (safe.Length <= 255)
    {
        return safe;
    }

    var ext = Path.GetExtension(safe);
    var stemLength = Math.Max(1, 255 - ext.Length);
    return safe[..stemLength] + ext;
}

static string BuildAddress(params string[] parts)
{
    return string.Join("", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
}

static string FirstNonEmpty(params string[] values)
{
    return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}

static void AddString(SqlCommand cmd, string name, string? value)
{
    cmd.Parameters.Add(name, SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
}

static string ReadString(SqlDataReader reader, string name)
{
    var ordinal = reader.GetOrdinal(name);
    return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
}

static int ReadInt(SqlDataReader reader, string name)
{
    var ordinal = reader.GetOrdinal(name);
    return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

static decimal ReadDecimal(SqlDataReader reader, string name)
{
    var ordinal = reader.GetOrdinal(name);
    return reader.IsDBNull(ordinal) ? 0 : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

static decimal? ReadNullableDecimal(SqlDataReader reader, string name)
{
    var ordinal = reader.GetOrdinal(name);
    return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

record LoginRequest(string Login, string Password);
record UserDto(string LoginID, string LoginCode, string LoginName, string Mobile);
record DeliveryDto(
    string Id,
    string CarLoadBillCode,
    string CarLoadBillID,
    string BillCode,
    string BillDate,
    string CustomerName,
    string Contact,
    string Phone,
    string Address,
    string Route,
    decimal Distance,
    decimal? CustomerLongitude,
    decimal? CustomerLatitude,
    string CarName,
    string DriverName,
    string DriverID,
    int DispatchState,
    int BackState,
    string DeliveryStatus,
    int SortNum,
    string CompletedAt,
    IReadOnlyList<ProductDto> Products);
record ProductDto(string Id, string Name, string Barcode, string Standard, string Model, string Unit, decimal Quantity, string AuxiliaryQuantity);
record AccessoryImage(byte[] Bytes, string Name, string Comment);
