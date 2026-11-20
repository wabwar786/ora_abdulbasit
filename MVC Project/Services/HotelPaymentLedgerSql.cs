using System;

namespace Orapmshms.Services;

/// <summary>
/// Authoritative read-only payment ledger used by both Dashboard and Night Audit.
/// Net receipts = positive receipts less negative refunds (security deposits excluded).
/// When hotel closing is enabled, only cb_status=2 posted entries are counted.
/// Use the SAME SQL fragment in summary, drill-down, trends and audit reports.
/// </summary>
internal static class HotelPaymentLedgerSql
{
    public const string Prefix = @"
DECLARE @isClosing bit = 0;
SELECT TOP (1) @isClosing = CASE WHEN TRY_CONVERT(int, ISNULL(isClosing,0)) = 1 THEN 1 ELSE 0 END
FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;

;WITH PaymentLedger AS
(
    SELECT pl.ID AS id,
           CONVERT(varchar(100),pl.currentdate) AS transaction_date,
           COALESCE(
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),100),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),103),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),110),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),101),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),120),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''),23),
               TRY_CONVERT(date,NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))),''))
           ) AS tx_date,
           COALESCE(TRY_CONVERT(decimal(18,2),
               NULLIF(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                   LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))),
                   ',',''), '£',''), '$',''), 'Rs.',''), 'PKR',''), 'GBP',''),'')),0) AS amount,
           COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),pl.payment_method))),''),'Unknown') AS method,
           ISNULL(CONVERT(varchar(100),pl.status),'') AS [status]
    FROM dbo.PaymentsLogTB pl
    WHERE pl.hotel_id=@hotel
      AND (@isClosing=0 OR LTRIM(RTRIM(CONVERT(varchar(20),pl.cb_status)))='2')
      -- Refundable security held on behalf of a guest is not hotel income.
      AND LOWER(ISNULL(CONVERT(nvarchar(120),pl.name),'')) NOT LIKE '%security%'
)";
}
