using System;
using System.Diagnostics;
using System.Web.Hosting;

namespace hotelsoftware.Utilities
{
    /// <summary>
    /// Central background dispatcher for availability calculation + Channex availability work.
    ///
    /// Performance-only behavior:
    /// - Existing availability calculation and Channex upload services are unchanged.
    /// - No requested job is skipped or deduplicated.
    /// - Jobs for the same hotel are serialized to prevent concurrent SQL pressure.
    /// - Different hotels can still process in parallel.
    /// - Callers can preserve their original dropdown index/value behavior for Channex.
    /// </summary>
    public static class CalendarAvailabilityBackgroundRunner
    {
        /// <summary>
        /// Preserves the existing:
        /// A availability -> A Channex -> B availability -> B Channex sequence.
        /// Used by room swap and room move/change flows.
        /// </summary>
        public static void Queue(
            string connectionString,
            string hotelid,
            string userName,
            string userId,
            string propertyId,
            string apiBaseUrl,
            string apiKey,
            DateTime aArr,
            DateTime aDep,
            string aCatLocal,
            DateTime bArr,
            DateTime bDep,
            string bCatLocal,
            Action<string, string, string, string, string> actionLogFn,
            string hotelName = "",
            string originPage = "",
            string originFunction = "")
        {
            HostingEnvironment.QueueBackgroundWorkItem(async cancellationToken =>
            {
                try
                {
                    await HotelAvailabilityJobCoordinator.RunAsync(
                        hotelid,
                        cancellationToken,
                        () =>
                        {
                            var availabilityService =
                                new AvailabilityBackgroundService(connectionString);

                            var channelManagerService =
                                new ChannelManagerBackgroundService(connectionString);

                            // Preserve the calendar's original behavior exactly:
                            // Channex index = 0 and selected value = category local id.
                            ExecuteRange(
                                availabilityService,
                                channelManagerService,
                                hotelid,
                                userName,
                                userId,
                                propertyId,
                                apiBaseUrl,
                                apiKey,
                                aArr,
                                aDep,
                                aCatLocal,
                                0,
                                aCatLocal,
                                "",
                                actionLogFn,
                                hotelName,
                                originPage,
                                originFunction);

                            ExecuteRange(
                                availabilityService,
                                channelManagerService,
                                hotelid,
                                userName,
                                userId,
                                propertyId,
                                apiBaseUrl,
                                apiKey,
                                bArr,
                                bDep,
                                bCatLocal,
                                0,
                                bCatLocal,
                                "",
                                actionLogFn,
                                hotelName,
                                originPage,
                                originFunction);
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Trace.TraceWarning(
                        "Availability background job cancelled. Hotel={0}; Origin={1}.{2}",
                        hotelid ?? "",
                        originPage ?? "",
                        originFunction ?? "");
                }
                catch (Exception ex)
                {
                    Trace.TraceError(
                        "Availability background job failed. Hotel={0}; Origin={1}.{2}; Error={3}",
                        hotelid ?? "",
                        originPage ?? "",
                        originFunction ?? "",
                        ex);
                }
            });
        }

        /// <summary>
        /// Backward-compatible single-range entry point used by the FrontDesk calendar.
        /// Preserves the previous runner behavior: Channex dropdown index = 0 and
        /// dropdown value = categoryLocalId; background IP remains empty.
        /// </summary>
        public static void QueueSingle(
            string connectionString,
            string hotelid,
            string userName,
            string userId,
            string propertyId,
            string apiBaseUrl,
            string apiKey,
            DateTime start,
            DateTime end,
            string categoryLocalId,
            Action<string, string, string, string, string> actionLogFn,
            string hotelName = "",
            string originPage = "",
            string originFunction = "")
        {
            QueueSingle(
                connectionString,
                hotelid,
                userName,
                userId,
                propertyId,
                apiBaseUrl,
                apiKey,
                start,
                end,
                categoryLocalId,
                0,
                categoryLocalId,
                "",
                actionLogFn,
                hotelName,
                originPage,
                originFunction);
        }

        /// <summary>
        /// Single-range availability entry point that preserves the caller's exact
        /// room-dropdown selection semantics and captured IP address.
        ///
        /// This overload is used by AvailabilitySetup because that page previously passed:
        /// - ddlrooms.SelectedIndex to ChannelManagerBackgroundService
        /// - ddlrooms.SelectedValue to ChannelManagerBackgroundService
        /// - the captured request IP to AvailabilityBackgroundService
        ///
        /// Keeping those values prevents any functional change while still applying
        /// per-hotel concurrency control.
        /// </summary>
        public static void QueueSingle(
            string connectionString,
            string hotelid,
            string userName,
            string userId,
            string propertyId,
            string apiBaseUrl,
            string apiKey,
            DateTime start,
            DateTime end,
            string availabilityCategoryLocalId,
            int ddlroomsSelectedIndex,
            string ddlroomsSelectedValue,
            string ip,
            Action<string, string, string, string, string> actionLogFn,
            string hotelName = "",
            string originPage = "",
            string originFunction = "")
        {
            HostingEnvironment.QueueBackgroundWorkItem(async cancellationToken =>
            {
                try
                {
                    await HotelAvailabilityJobCoordinator.RunAsync(
                        hotelid,
                        cancellationToken,
                        () =>
                        {
                            var availabilityService =
                                new AvailabilityBackgroundService(connectionString);

                            var channelManagerService =
                                new ChannelManagerBackgroundService(connectionString);

                            ExecuteRange(
                                availabilityService,
                                channelManagerService,
                                hotelid,
                                userName,
                                userId,
                                propertyId,
                                apiBaseUrl,
                                apiKey,
                                start,
                                end,
                                availabilityCategoryLocalId,
                                ddlroomsSelectedIndex,
                                ddlroomsSelectedValue,
                                ip,
                                actionLogFn,
                                hotelName,
                                originPage,
                                originFunction);
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Trace.TraceWarning(
                        "Availability background job cancelled. Hotel={0}; Origin={1}.{2}",
                        hotelid ?? "",
                        originPage ?? "",
                        originFunction ?? "");
                }
                catch (Exception ex)
                {
                    Trace.TraceError(
                        "Availability background job failed. Hotel={0}; Origin={1}.{2}; Error={3}",
                        hotelid ?? "",
                        originPage ?? "",
                        originFunction ?? "",
                        ex);
                }
            });
        }

        private static void ExecuteRange(
            AvailabilityBackgroundService availabilityService,
            ChannelManagerBackgroundService channelManagerService,
            string hotelid,
            string userName,
            string userId,
            string propertyId,
            string apiBaseUrl,
            string apiKey,
            DateTime start,
            DateTime end,
            string availabilityCategoryLocalId,
            int ddlroomsSelectedIndex,
            string ddlroomsSelectedValue,
            string ip,
            Action<string, string, string, string, string> actionLogFn,
            string hotelName,
            string originPage,
            string originFunction)
        {
            availabilityService.AutoUpdateAvailability(
                start,
                end,
                hotelid,
                userName,
                ip ?? "",
                availabilityCategoryLocalId,
                userId,
                "",
                actionLogFn,
                hotelName,
                originPage,
                originFunction);

            channelManagerService.UploadToChannelManager(
                hotelid,
                start,
                end,
                ddlroomsSelectedIndex,
                ddlroomsSelectedValue,
                propertyId,
                apiBaseUrl,
                apiKey,
                "",
                "",
                hotelName,
                originPage,
                originFunction);
        }
    }
}
