<%@ Page Title="" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true"
    CodeBehind="BulkUpdateRestrictions.aspx.cs" Inherits="hotelsoftware.BulkUpdateRestrictions" %>

<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" />
    <link href="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/css/select2.min.css" rel="stylesheet" />
    <link href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css" rel="stylesheet" />

    <style>
        body { background: #f4f6fb; }
        .top-bar { height: 8px; background: linear-gradient(90deg, #2f6ce5, #14b8a6); }

        .card-wrap {
            background: #fff;
            border: 1px solid #e5e7eb;
            border-radius: 10px;
            padding: 18px;
            margin: 12px auto;
            box-shadow: 0 8px 24px rgba(15, 23, 42, .06);
        }

        .title-row {
            display: flex;
            align-items: center;
            gap: 10px;
            margin-bottom: 14px;
            color: #1f2937;
            font-size: 16px;
            font-weight: 800;
            letter-spacing: .3px;
        }

        .field-label {
            margin: 0 0 4px;
            color: #374151;
            font-size: 12px;
            font-weight: 700;
        }

        .field-help { color: #6b7280; font-size: 11px; }

        .select-row {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            gap: 8px;
            align-items: end;
            margin-top: 10px;
        }

        .select2-container--default .select2-selection--multiple {
            min-height: 34px;
            border: 1px solid #cbd5e1 !important;
            border-radius: 6px !important;
            padding: 1px 44px 1px 6px;
        }

        .select2-container--default .select2-selection__choice {
            margin-top: 4px !important;
            padding: 2px 8px !important;
            border: 0 !important;
            border-radius: 4px !important;
            background: #e8eef9 !important;
        }

        .select2-container--default .select2-selection__choice__remove { margin-right: 5px !important; }

        .action-buttons { display: flex; gap: 6px; }

        .btn-ico {
            width: 34px;
            height: 34px;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            border: 0;
            border-radius: 6px;
            cursor: pointer;
        }

        .btn-plus { background: #16a34a; color: #fff; }
        .btn-x { background: #e11d48; color: #fff; }

        .section-box {
            margin-top: 14px;
            padding: 12px;
            border: 1px solid #e5e7eb;
            border-radius: 8px;
            background: #fff;
        }

        .section-heading {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 10px;
            margin-bottom: 8px;
        }

        .range-row {
            display: grid;
            grid-template-columns: 120px minmax(0, 1fr) 38px;
            align-items: center;
            margin-top: 8px;
            border: 1px solid #e5e7eb;
            border-radius: 6px;
            overflow: hidden;
            background: #fff;
        }

        .badge-small {
            min-height: 34px;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            padding: 6px 10px;
            background: #1d3557;
            color: #fff;
            font-size: 11px;
            white-space: nowrap;
        }

        .range-date .input-group .form-control {
            height: 34px;
            border: 0;
            border-left: 1px solid #e5e7eb;
            border-right: 1px solid #e5e7eb;
            border-radius: 0;
            font-size: 12px;
        }

        .range-date .cal-btn {
            height: 34px;
            border: 0;
            border-radius: 0;
            background: #f9a917;
            cursor: pointer;
        }

        .btnDelRange {
            width: 38px;
            height: 34px;
            border-radius: 0;
        }

        .days-box {
            display: flex;
            flex-wrap: wrap;
            gap: 6px;
        }

        .day-pill {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            margin: 0;
            padding: 6px 10px;
            border: 1px solid #dbeafe;
            border-radius: 5px;
            background: #eef2ff;
            font-size: 12px;
            cursor: pointer;
        }

        .restriction-grid {
            display: grid;
            grid-template-columns: repeat(3, minmax(230px, 1fr));
            gap: 10px;
        }

        .restriction-card {
            min-height: 90px;
            padding: 10px;
            border: 1px solid #e5e7eb;
            border-radius: 7px;
            background: #fafcff;
        }

        .restriction-card.disabled-card { opacity: .55; }

        .cutoff-custom-field {
            margin-top: 7px;
        }

        .cutoff-custom-field.d-none {
            display: none !important;
        }

        .restriction-top {
            display: flex;
            align-items: center;
            gap: 8px;
            margin-bottom: 8px;
        }

        .restriction-top .form-check-input {
            width: 38px;
            height: 20px;
            margin: 0;
            cursor: pointer;
        }

        .restriction-title { font-size: 12px; font-weight: 700; color: #1f2937; }

        .restriction-card input[type=number],
        .restriction-card select {
            height: 34px;
            border: 1px solid #cbd5e1;
            border-radius: 5px;
            font-size: 12px;
        }

        .clear-box {
            display: flex;
            align-items: flex-start;
            gap: 9px;
            padding: 10px;
            border: 1px solid #fecaca;
            border-radius: 7px;
            background: #fff7f7;
        }

        .clear-box input { margin-top: 3px; }

        .summary-title {
            margin-top: 16px;
            color: #111827;
            font-size: 13px;
            font-weight: 900;
        }

        table.summary { min-width: 1180px; margin-bottom: 0; }
        table.summary thead th {
            border: 0;
            background: #c8eeee;
            color: #0f172a;
            font-size: 11px;
            font-weight: 800;
            white-space: nowrap;
        }
        table.summary td {
            border-color: #e5e7eb;
            font-size: 11px;
            vertical-align: middle;
        }
        table.summary tbody tr:nth-child(even) { background: #f8fbff; }

        .footer-actions {
            display: flex;
            align-items: center;
            gap: 8px;
            margin-top: 12px;
        }

        .btn-save, .btn-cancel {
            border: 0;
            border-radius: 6px;
            padding: 7px 12px;
            color: #fff;
            font-size: 13px;
        }
        .btn-save { background: #20b996; }
        .btn-cancel { background: #ef4444; }
        .btn-save:disabled { opacity: .6; cursor: not-allowed; }

        #lblStatus { transition: all .2s ease; }
        #lblStatus.bad { background: #ef4444 !important; }
        #lblStatus.ok { background: #16a34a !important; }

        #rangeCalOverlay {
            display: none;
            position: fixed;
            inset: 0;
            z-index: 999998;
            background: rgba(0, 0, 0, .25);
            backdrop-filter: blur(2px);
        }

        #rangeCalPopup {
            display: none;
            position: fixed;
            top: 100px;
            left: 50%;
            z-index: 999999;
            width: 860px;
            max-width: calc(100vw - 20px);
            padding: 14px;
            transform: translateX(-50%);
            border: 1px solid #e5e7eb;
            border-radius: 8px;
            background: #fff;
            box-shadow: 0 16px 40px rgba(0, 0, 0, .18);
        }

        .rcal-header { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 4px 6px 10px; }
        .rcal-month-title { min-width: 140px; display: flex; justify-content: center; color: #111827; font-size: 14px; font-weight: 700; }
        .rcal-nav { display: flex; align-items: center; gap: 8px; }
        .rcal-btn { width: 34px; height: 34px; display: flex; align-items: center; justify-content: center; border: 1px solid #d1d5db; border-radius: 6px; background: #fff; cursor: pointer; }
        .rcal-btn:hover { background: #f3f4f6; }
        .rcal-body { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; padding: 0 6px; }
        .rcal-panel { padding: 10px; border: 1px solid #e5e7eb; border-radius: 7px; background: #fff; }
        .rcal-dow { display: grid; grid-template-columns: repeat(7, 1fr); gap: 6px; padding: 4px 2px 10px; color: #6b7280; font-size: 12px; font-weight: 700; text-align: center; }
        .rcal-grid { display: grid; grid-template-columns: repeat(7, 1fr); gap: 6px; }
        .rcal-day { height: 34px; display: flex; align-items: center; justify-content: center; border: 1px solid transparent; border-radius: 5px; background: #fff; color: #111827; font-size: 12px; font-weight: 600; line-height: 1; cursor: pointer; user-select: none; }
        .rcal-day.is-other { visibility: hidden; pointer-events: none; }
        .rcal-day:hover:not(.is-other) { background: #eef2ff; border-color: #bfdbfe; }
        .rcal-day.is-today:not(.is-start):not(.is-end) { background: #fff7ed; border-color: #f59e0b; color: #b45309; font-weight: 800; box-shadow: inset 0 0 0 1px #f59e0b; }
        .rcal-day.is-start, .rcal-day.is-end { background: #1d4ed8; border-color: #1d4ed8; color: #fff; }
        .rcal-day.is-start.is-today, .rcal-day.is-end.is-today { box-shadow: inset 0 0 0 2px #fbbf24; }
        .rcal-day.is-inrange { background: #dbeafe; border-color: #dbeafe; color: #111827; }
        .rcal-footer { display: flex; align-items: center; justify-content: flex-end; gap: 10px; padding: 12px 6px 2px; }
        .rcal-rangeText { margin-right: auto; color: #111827; font-size: 12px; font-weight: 700; }
        .rcal-action { padding: 6px 12px; border: 1px solid #d1d5db; border-radius: 6px; background: #fff; font-size: 12px; font-weight: 700; cursor: pointer; }
        .rcal-apply { border: 0; background: #06b6d4; color: #fff; }

        .mt-4, .mt-lg-3 { margin-top: 0 !important; }

        @media (max-width: 1000px) {
            .restriction-grid { grid-template-columns: repeat(2, minmax(220px, 1fr)); }
            #rangeCalPopup { top: 70px; }
        }

        @media (max-width: 720px) {
            .card-wrap { padding: 12px; }
            .restriction-grid { grid-template-columns: 1fr; }
            .range-row { grid-template-columns: 90px minmax(0, 1fr) 38px; }
            .rcal-body { grid-template-columns: 1fr; }
            .rcal-panel:nth-child(2) { display: none; }
            .footer-actions { flex-wrap: wrap; }
            .footer-actions .ms-auto { width: 100%; margin-left: 0 !important; }
        }
    </style>
</asp:Content>

<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder2" runat="server">
    <div class="top-bar"></div>

    <asp:HiddenField ID="hdHotelId" runat="server" />
    <asp:HiddenField ID="hdUserId" runat="server" />
    <asp:HiddenField ID="hdUserName" runat="server" />

    <div class="card-wrap">
        <div class="title-row">
            <i class="fa-solid fa-sliders"></i>
            <div>BULK UPDATE RESTRICTIONS</div>
        </div>

        <div class="d-none">
            <div class="field-label">Rate Plan Buckets</div>
            <select id="ddlBuckets" class="form-select" multiple="multiple" style="width:100%"></select>
        </div>

        <div class="select-row">
            <div>
                <div class="field-label">Rate Plans</div>
                <select id="ddlPlans" class="form-select" multiple="multiple" style="width:100%"></select>
            </div>
            <div class="action-buttons">
                <button type="button" class="btn-ico btn-plus" id="btnPlansPlus" title="Select all rate plans"><i class="fa-solid fa-plus"></i></button>
                <button type="button" class="btn-ico btn-x" id="btnPlansClear" title="Clear rate plans"><i class="fa-solid fa-xmark"></i></button>
            </div>
        </div>

        <div class="select-row">
            <div>
                <div class="field-label">Room Types</div>
                <select id="ddlRoomTypes" class="form-select" multiple="multiple" style="width:100%"></select>
            </div>
            <div class="action-buttons">
                <button type="button" class="btn-ico btn-plus" id="btnRoomsPlus" title="Select all room types"><i class="fa-solid fa-plus"></i></button>
                <button type="button" class="btn-ico btn-x" id="btnRoomsClear" title="Clear room types"><i class="fa-solid fa-xmark"></i></button>
            </div>
        </div>

        <div class="section-box">
            <div class="section-heading">
                <div>
                    <div class="field-label">Date Ranges</div>
                    <div class="field-help">You can add multiple ranges. Overlapping dates are saved only once.</div>
                </div>
                <a class="fw-bold text-decoration-none" style="color:#06b6d4;cursor:pointer;font-size:12px;" id="btnAddRange">
                    <i class="fa-solid fa-plus"></i> Add Range
                </a>
            </div>
            <div id="rangesArea"></div>
        </div>

        <div class="section-box">
            <div class="section-heading">
                <div>
                    <div class="field-label">Applicable Days</div>
                    <div class="field-help">Only selected weekdays inside each date range will be updated.</div>
                </div>
                <div class="action-buttons">
                    <button type="button" class="btn btn-sm btn-outline-success" id="btnDaysAll">All</button>
                    <button type="button" class="btn btn-sm btn-outline-danger" id="btnDaysClear">Clear</button>
                </div>
            </div>
            <div class="days-box">
                <label class="day-pill"><input type="checkbox" class="dayChk" value="0" checked /> Sunday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="1" checked /> Monday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="2" checked /> Tuesday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="3" checked /> Wednesday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="4" checked /> Thursday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="5" checked /> Friday</label>
                <label class="day-pill"><input type="checkbox" class="dayChk" value="6" checked /> Saturday</label>
            </div>
        </div>

        <div class="section-box">
            <div class="section-heading">
                <div>
                    <div class="field-label">Restrictions</div>
                    <div class="field-help">Unchecked numeric fields and “No Change” dropdowns preserve the current database value.</div>
                </div>
            </div>

            <div class="restriction-grid" id="restrictionGrid">
                <div class="restriction-card numeric-card" data-switch="swMinStayArrival" data-input="txtMinStayArrival">
                    <div class="restriction-top">
                        <input class="form-check-input" type="checkbox" id="swMinStayArrival" />
                        <div class="restriction-title">Minimum Stay on Arrival</div>
                    </div>
                    <input type="number" class="form-control" id="txtMinStayArrival" min="1" step="1" value="1" disabled />
                    <div class="field-help mt-1">OTA's: min_stay_arrival. Use 1 to clear.</div>
                </div>

                <div class="restriction-card numeric-card" data-switch="swMinStayThrough" data-input="txtMinStayThrough">
                    <div class="restriction-top">
                        <input class="form-check-input" type="checkbox" id="swMinStayThrough" />
                        <div class="restriction-title">Minimum Stay Through</div>
                    </div>
                    <input type="number" class="form-control" id="txtMinStayThrough" min="1" step="1" value="1" disabled />
                    <div class="field-help mt-1">OTA's: min_stay_through. Use 1 to clear.</div>
                </div>

                <div class="restriction-card numeric-card" data-switch="swMaxStay" data-input="txtMaxStay">
                    <div class="restriction-top">
                        <input class="form-check-input" type="checkbox" id="swMaxStay" />
                        <div class="restriction-title">Maximum Stay</div>
                    </div>
                    <input type="number" class="form-control" id="txtMaxStay" min="0" step="1" value="0" disabled />
                    <div class="field-help mt-1">OTA's: max_stay. Use 0 to clear.</div>
                </div>

                <div class="restriction-card cutoff-card">
                    <div class="restriction-title mb-2">
                        Booking Cutoff
                        <i class="fa-solid fa-circle-question ms-1 text-secondary"
                           title="Controls how many calendar days before arrival this selected rate plan must stop selling."></i>
                    </div>

                    <select id="ddlCutoffMode" class="form-select"
                            title="Choose whether to preserve, inherit, disable, or override the rate-plan cutoff.">
                        <option value="None" selected>No Change</option>
                        <option value="PlanDefault">Use Rate Plan Default</option>
                        <option value="Disabled">Disable for Selected Dates</option>
                        <option value="Custom">Set Custom Cutoff Days</option>
                    </select>

                    <div id="cutoffCustomField" class="cutoff-custom-field d-none">
                        <input type="number"
                               class="form-control"
                               id="txtCutoff"
                               min="1"
                               max="365"
                               step="1"
                               inputmode="numeric"
                               value="1"
                               disabled
                               title="Enter a whole number between 1 and 365." />
                    </div>

                    <div class="field-help mt-1">
                        The effective cutoff becomes a rate-plan-specific Stop Sell. Other rate plans remain open.
                    </div>
                </div>

                <div class="restriction-card status-card">
                    <div class="restriction-title mb-2">Closed to Arrival</div>
                    <select id="ddlClosedToArrival" class="form-select">
                        <option value="None" selected>No Change</option>
                        <option value="Open">Allow Arrival</option>
                        <option value="Closed">Close Arrival</option>
                    </select>
                    <div class="field-help mt-1">Controls whether guests may check in on selected dates.</div>
                </div>

                <div class="restriction-card status-card">
                    <div class="restriction-title mb-2">Closed to Departure</div>
                    <select id="ddlClosedToDeparture" class="form-select">
                        <option value="None" selected>No Change</option>
                        <option value="Open">Allow Departure</option>
                        <option value="Closed">Close Departure</option>
                    </select>
                    <div class="field-help mt-1">Controls whether guests may check out on selected dates.</div>
                </div>

                <div class="restriction-card status-card">
                    <div class="restriction-title mb-2">Selling Status / Stop Sell</div>
                    <select id="ddlSellingStatus" class="form-select">
                        <option value="None" selected>No Change</option>
                        <option value="Open">Open for Sale</option>
                        <option value="Closed">Stop Sell</option>
                    </select>
                    <div class="field-help mt-1">Open sends stop_sell=false; Closed sends stop_sell=true.</div>
                </div>
            </div>

            <div class="clear-box mt-3">
                <input type="checkbox" id="chkClearAll" />
                <div>
                    <label for="chkClearAll" class="field-label" style="cursor:pointer;">Clear all supported restrictions</label>
                    <div class="field-help">
                        Sends Min Arrival=1, Min Through=1, Max Stay=0, CTA=false, CTD=false and manual Stop Sell=false. Booking cutoff is disabled for the selected dates.
                    </div>
                </div>
            </div>
        </div>

        <div class="summary-title">UPDATE SUMMARY</div>
        <div class="table-responsive mt-2">
            <table class="table summary" id="tblSummary">
                <thead>
                    <tr>
                        <th>Date Range</th>
                        <th>Room Type</th>
                        <th>Rate Plan</th>
                        <th>Min Arrival</th>
                        <th>Min Through</th>
                        <th>Max Stay</th>
                        <th>Cutoff</th>
                        <th>CTA</th>
                        <th>CTD</th>
                        <th>Stop Sell</th>
                    </tr>
                </thead>
                <tbody>
                    <tr><td colspan="10" class="text-center text-muted">Select plans, room types and a date range to preview.</td></tr>
                </tbody>
            </table>
        </div>

        <div class="footer-actions">
            <button type="button" class="btn-save" id="btnSave"><i class="fa-solid fa-floppy-disk"></i> Save Restrictions</button>
            <button type="button" class="btn-cancel" id="btnCancel"><i class="fa-solid fa-circle-xmark"></i> Cancel</button>

            <div class="ms-auto d-flex align-items-center gap-2">
                <span class="text-muted small">Status:</span>
                <span id="lblStatus" class="badge-small ok">Ready</span>
            </div>
        </div>
    </div>

    <div id="rangeCalOverlay"></div>
    <div id="rangeCalPopup">
        <div class="rcal-header">
            <div class="rcal-nav"><div class="rcal-btn" id="rcalPrev">&#8249;</div></div>
            <div class="rcal-month-title" id="rcalLeftTitle"></div>
            <div class="rcal-month-title" id="rcalRightTitle"></div>
            <div class="rcal-nav"><div class="rcal-btn" id="rcalNext">&#8250;</div></div>
        </div>
        <div class="rcal-body">
            <div class="rcal-panel">
                <div class="rcal-dow"><div>Su</div><div>Mo</div><div>Tu</div><div>We</div><div>Th</div><div>Fr</div><div>Sa</div></div>
                <div class="rcal-grid" id="rcalLeftGrid"></div>
            </div>
            <div class="rcal-panel">
                <div class="rcal-dow"><div>Su</div><div>Mo</div><div>Tu</div><div>We</div><div>Th</div><div>Fr</div><div>Sa</div></div>
                <div class="rcal-grid" id="rcalRightGrid"></div>
            </div>
        </div>
        <div class="rcal-footer">
            <div class="rcal-rangeText" id="rcalRangeText">Select date range</div>
            <button type="button" class="rcal-action" id="rcalClear">Clear</button>
            <button type="button" class="rcal-action rcal-apply" id="rcalApply">Apply</button>
        </div>
    </div>

    <script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/js/select2.min.js"></script>

    <script>
        // Calendar is isolated in its own namespace so functions from the Master Page
        // or another calendar library cannot replace its rendering functions.
        window.BURCalendar = (function ($) {
            "use strict";

            let targetTextId = null;
            let targetStartId = null;
            let targetEndId = null;
            let viewYear = (new Date()).getFullYear();
            let viewMonth = (new Date()).getMonth();
            let selectedStart = null;
            let selectedEnd = null;

            const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

            function pad2(value) {
                return (value < 10 ? "0" : "") + value;
            }

            function isoToUK(iso) {
                if (!iso) return "";
                const parts = String(iso).split("-");
                if (parts.length !== 3) return "";
                return parts[2] + "/" + parts[1] + "/" + parts[0];
            }

            function ukToIso(uk) {
                if (!uk) return "";
                const parts = String(uk).split("/");
                if (parts.length !== 3) return "";

                const day = parseInt(parts[0], 10);
                const month = parseInt(parts[1], 10);
                const year = parseInt(parts[2], 10);

                if (!Number.isInteger(day) || !Number.isInteger(month) || !Number.isInteger(year)) return "";
                const date = new Date(year, month - 1, day);
                if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return "";

                return year + "-" + pad2(month) + "-" + pad2(day);
            }

            function dateToIso(date) {
                return date.getFullYear() + "-" + pad2(date.getMonth() + 1) + "-" + pad2(date.getDate());
            }

            function dateToUK(date) {
                return pad2(date.getDate()) + "/" + pad2(date.getMonth() + 1) + "/" + date.getFullYear();
            }

            function parseIso(iso) {
                if (!iso) return null;
                const parts = String(iso).split("-");
                if (parts.length !== 3) return null;

                const year = parseInt(parts[0], 10);
                const month = parseInt(parts[1], 10);
                const day = parseInt(parts[2], 10);
                const date = new Date(year, month - 1, day);

                if (isNaN(date.getTime())) return null;
                if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null;
                return date;
            }

            function isSameDay(first, second) {
                return !!first && !!second &&
                    first.getFullYear() === second.getFullYear() &&
                    first.getMonth() === second.getMonth() &&
                    first.getDate() === second.getDate();
            }

            function getDaysInMonth(year, month) {
                return new Date(year, month + 1, 0).getDate();
            }

            function isInsideSelectedRange(date) {
                if (!selectedStart || !selectedEnd) return false;
                const time = date.getTime();
                return time > selectedStart.getTime() && time < selectedEnd.getTime();
            }

            function updateRangeText() {
                const $text = $("#rcalRangeText");
                if (!selectedStart && !selectedEnd) {
                    $text.text("Select date range");
                } else if (selectedStart && !selectedEnd) {
                    $text.text(dateToUK(selectedStart) + " - ...");
                } else {
                    $text.text(dateToUK(selectedStart) + " - " + dateToUK(selectedEnd));
                }
            }

            function appendEmptyDay(gridElement) {
                const empty = document.createElement("div");
                empty.className = "rcal-day is-other";
                empty.setAttribute("aria-hidden", "true");
                gridElement.appendChild(empty);
            }

            function appendCalendarDay(gridElement, date, today) {
                const dayElement = document.createElement("div");
                let className = "rcal-day";

                if (isSameDay(date, today)) className += " is-today";
                if (isSameDay(date, selectedStart)) className += " is-start";
                else if (isSameDay(date, selectedEnd)) className += " is-end";
                else if (isInsideSelectedRange(date)) className += " is-inrange";

                dayElement.className = className;
                dayElement.dataset.iso = dateToIso(date);
                dayElement.textContent = String(date.getDate());
                dayElement.setAttribute("role", "button");
                dayElement.setAttribute("tabindex", "0");
                dayElement.setAttribute("aria-label", dateToUK(date) + (isSameDay(date, today) ? " (Today)" : ""));
                dayElement.title = dateToUK(date) + (isSameDay(date, today) ? " - Today" : "");
                gridElement.appendChild(dayElement);
            }

            function renderCalendarMonth(gridId, year, month) {
                const gridElement = document.getElementById(gridId);
                if (!gridElement) return;

                // Rebuild the complete grid every time. This prevents stale weekday
                // elements or markup from another calendar script appearing here.
                gridElement.replaceChildren();

                const firstDayOfWeek = new Date(year, month, 1).getDay();
                const totalDays = getDaysInMonth(year, month);
                const today = new Date();
                today.setHours(0, 0, 0, 0);

                for (let index = 0; index < firstDayOfWeek; index++) {
                    appendEmptyDay(gridElement);
                }

                for (let dayNumber = 1; dayNumber <= totalDays; dayNumber++) {
                    appendCalendarDay(gridElement, new Date(year, month, dayNumber), today);
                }

                const usedCells = firstDayOfWeek + totalDays;
                const trailingCells = (7 - (usedCells % 7)) % 7;
                for (let index = 0; index < trailingCells; index++) {
                    appendEmptyDay(gridElement);
                }
            }

            function renderCalendar() {
                $("#rcalLeftTitle").text(months[viewMonth] + " " + viewYear);

                let rightYear = viewYear;
                let rightMonth = viewMonth + 1;
                if (rightMonth > 11) {
                    rightMonth = 0;
                    rightYear++;
                }

                $("#rcalRightTitle").text(months[rightMonth] + " " + rightYear);
                renderCalendarMonth("rcalLeftGrid", viewYear, viewMonth);
                renderCalendarMonth("rcalRightGrid", rightYear, rightMonth);
                updateRangeText();
            }

            function openCalendar() {
                if ($("#rangeCalOverlay").parent()[0] !== document.body) $("#rangeCalOverlay").appendTo(document.body);
                if ($("#rangeCalPopup").parent()[0] !== document.body) $("#rangeCalPopup").appendTo(document.body);
                $("#rangeCalOverlay,#rangeCalPopup").show();
                renderCalendar();
            }

            function closeCalendar() {
                $("#rangeCalOverlay,#rangeCalPopup").hide();
            }

            function clearCalendar() {
                selectedStart = null;
                selectedEnd = null;
                renderCalendar();
            }

            function applyCalendar() {
                if (!targetTextId || !targetStartId || !targetEndId) return;
                if (!selectedStart || !selectedEnd) {
                    $("#rcalRangeText").text("Select start and end date");
                    return;
                }

                $("#" + targetStartId).val(dateToIso(selectedStart)).trigger("change");
                $("#" + targetEndId).val(dateToIso(selectedEnd)).trigger("change");
                $("#" + targetTextId).val(dateToUK(selectedStart) + " - " + dateToUK(selectedEnd)).trigger("change");
                closeCalendar();
            }

            function selectDay(iso) {
                const date = parseIso(iso);
                if (!date) return;

                if (!selectedStart && !selectedEnd) {
                    selectedStart = date;
                    selectedEnd = null;
                    renderCalendar();
                    return;
                }

                if (selectedStart && !selectedEnd) {
                    if (date.getTime() < selectedStart.getTime()) {
                        selectedEnd = selectedStart;
                        selectedStart = date;
                    } else {
                        selectedEnd = date;
                    }
                    renderCalendar();
                    return;
                }

                selectedStart = date;
                selectedEnd = null;
                renderCalendar();
            }

            function openFor(textId, startId, endId) {
                targetTextId = textId;
                targetStartId = startId;
                targetEndId = endId;
                selectedStart = parseIso($("#" + startId).val());
                selectedEnd = parseIso($("#" + endId).val());

                if (selectedStart) {
                    viewYear = selectedStart.getFullYear();
                    viewMonth = selectedStart.getMonth();
                } else {
                    const today = new Date();
                    viewYear = today.getFullYear();
                    viewMonth = today.getMonth();
                }

                openCalendar();
            }

            $(document)
                .off("click.burCalendar", "#rangeCalOverlay")
                .on("click.burCalendar", "#rangeCalOverlay", closeCalendar)
                .off("click.burCalendar", "#rcalPrev")
                .on("click.burCalendar", "#rcalPrev", function () {
                    viewMonth--;
                    if (viewMonth < 0) {
                        viewMonth = 11;
                        viewYear--;
                    }
                    renderCalendar();
                })
                .off("click.burCalendar", "#rcalNext")
                .on("click.burCalendar", "#rcalNext", function () {
                    viewMonth++;
                    if (viewMonth > 11) {
                        viewMonth = 0;
                        viewYear++;
                    }
                    renderCalendar();
                })
                .off("click.burCalendar", "#rcalClear")
                .on("click.burCalendar", "#rcalClear", clearCalendar)
                .off("click.burCalendar", "#rcalApply")
                .on("click.burCalendar", "#rcalApply", applyCalendar)
                .off("click.burCalendar keydown.burCalendar", "#rcalLeftGrid .rcal-day:not(.is-other),#rcalRightGrid .rcal-day:not(.is-other)")
                .on("click.burCalendar", "#rcalLeftGrid .rcal-day:not(.is-other),#rcalRightGrid .rcal-day:not(.is-other)", function () {
                    selectDay($(this).attr("data-iso"));
                })
                .on("keydown.burCalendar", "#rcalLeftGrid .rcal-day:not(.is-other),#rcalRightGrid .rcal-day:not(.is-other)", function (event) {
                    if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        selectDay($(this).attr("data-iso"));
                    }
                });

            $(document)
                .off("keydown.burCalendarPopup")
                .on("keydown.burCalendarPopup", function (event) {
                    if (event.key === "Escape") closeCalendar();
                });

            return {
                openFor: openFor,
                isoToUK: isoToUK,
                ukToIso: ukToIso
            };
        })(jQuery);

        window.openRangeCalendarFor = function (textId, startId, endId) {
            window.BURCalendar.openFor(textId, startId, endId);
        };

        let rangeIndex = 0;
        let previewTimer = null;
        let isSaving = false;

        function setStatus(text, good) {
            $("#lblStatus").text(text).toggleClass("ok", !!good).toggleClass("bad", !good);
        }

        function escapeHtml(value) {
            return String(value == null ? "" : value)
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;")
                .replace(/"/g, "&quot;")
                .replace(/'/g, "&#039;");
        }

        async function apiPost(method, data) {
            const url = "BulkUpdateRestrictions.aspx/" + method;
            try {
                return await $.ajax({
                    url: url,
                    type: "POST",
                    contentType: "application/json; charset=utf-8",
                    dataType: "json",
                    data: JSON.stringify(data || {}),
                    timeout: 60000
                });
            } catch (xhr) {
                console.error("AJAX failed:", url, xhr.status, xhr.responseText);
                let message = "Request failed (" + (xhr.status || 0) + ")";
                try {
                    const parsed = JSON.parse(xhr.responseText);
                    if (parsed && parsed.Message) message = parsed.Message;
                } catch (_) { }
                setStatus(message, false);
                return null;
            }
        }

        function unwrapResponse(response) {
            if (!response) return null;
            let value = Object.prototype.hasOwnProperty.call(response, "d") ? response.d : response;
            if (typeof value === "string") {
                try { value = JSON.parse(value); } catch (_) { }
            }
            return value;
        }

        function addRangeRow() {
            rangeIndex++;
            const txtId = "txt_dateRange_" + rangeIndex;
            const startId = "hfStartDate_" + rangeIndex;
            const endId = "hfEndDate_" + rangeIndex;

            const html = `
                <div class="range-row">
                    <div class="badge-small">Date Range</div>
                    <div class="range-date">
                        <div class="input-group">
                            <input type="text" id="${txtId}" class="form-control dateRangeTxt" readonly placeholder="dd/mm/yyyy - dd/mm/yyyy" />
                            <input type="hidden" id="${startId}" class="hfStartDate" />
                            <input type="hidden" id="${endId}" class="hfEndDate" />
                            <span class="input-group-text cal-btn" title="Select date range"
                                onclick="openRangeCalendarFor('${txtId}','${startId}','${endId}')"><i class="fa-regular fa-calendar-days"></i></span>
                        </div>
                    </div>
                    <button type="button" class="btn-ico btn-x btnDelRange" title="Delete range"><i class="fa-solid fa-trash"></i></button>
                </div>`;

            $("#rangesArea").append(html);
        }

        function getSelectedDays() {
            return $(".dayChk:checked").map(function () { return parseInt(this.value, 10); }).get();
        }

        function getRangesPayload() {
            const ranges = [];
            $("#rangesArea .range-row").each(function () {
                let start = $(this).find(".hfStartDate").val();
                let end = $(this).find(".hfEndDate").val();
                if (!start || !end) {
                    const text = ($(this).find(".dateRangeTxt").val() || "").trim();
                    const parts = text.split("-");
                    if (parts.length === 2) {
                        start = window.BURCalendar.ukToIso(parts[0].trim());
                        end = window.BURCalendar.ukToIso(parts[1].trim());
                    }
                }
                ranges.push({ start: start || "", end: end || "" });
            });
            return ranges;
        }

        function nullableNumber(switchSelector, inputSelector) {
            if (!$(switchSelector).is(":checked") || $("#chkClearAll").is(":checked")) return null;
            const raw = $(inputSelector).val();
            if (raw === "" || raw == null) return null;
            const parsed = parseInt(raw, 10);
            return Number.isFinite(parsed) ? parsed : null;
        }

        function buildRequest() {
            const clearAll = $("#chkClearAll").is(":checked");
            const cutoffMode = clearAll ? "Disabled" : ($("#ddlCutoffMode").val() || "None");
            const cutoffRaw = $("#txtCutoff").val();
            const cutoffValue = cutoffMode === "Custom" && cutoffRaw !== "" && cutoffRaw != null
                ? parseInt(cutoffRaw, 10)
                : null;

            return {
                buckets: $("#ddlBuckets").val() || [],
                plans: $("#ddlPlans").val() || [],
                rooms: $("#ddlRoomTypes").val() || [],
                ranges: getRangesPayload(),
                days: getSelectedDays(),
                clearAll: clearAll,
                minStayArrival: nullableNumber("#swMinStayArrival", "#txtMinStayArrival"),
                minStayThrough: nullableNumber("#swMinStayThrough", "#txtMinStayThrough"),
                maxStay: nullableNumber("#swMaxStay", "#txtMaxStay"),
                cutoffMode: cutoffMode,
                cutoff: Number.isFinite(cutoffValue) ? cutoffValue : null,
                closedToArrivalStatus: clearAll ? "Open" : ($("#ddlClosedToArrival").val() || "None"),
                closedToDepartureStatus: clearAll ? "Open" : ($("#ddlClosedToDeparture").val() || "None"),
                sellingStatus: clearAll ? "Open" : ($("#ddlSellingStatus").val() || "None")
            };
        }

        function validateRequest(req, forSave) {
            if (!req.plans.length) return "Select at least one Rate Plan";
            if (!req.rooms.length) return "Select at least one Room Type";
            if (!req.ranges.length) return "Add a Date Range";
            if (!req.days.length) return "Select at least one Applicable Day";

            for (const range of req.ranges) {
                if (!range.start || !range.end) return "Select every date range using the calendar";
                if (range.end < range.start) return "End date must be on or after Start date";
            }

            if (req.clearAll) return null;

            if (req.minStayArrival !== null && req.minStayArrival < 1) return "Minimum Stay on Arrival must be 1 or greater";
            if (req.minStayThrough !== null && req.minStayThrough < 1) return "Minimum Stay Through must be 1 or greater";
            if (req.maxStay !== null && req.maxStay < 0) return "Maximum Stay cannot be negative";

            const validCutoffModes = ["None", "PlanDefault", "Disabled", "Custom"];
            if (!validCutoffModes.includes(req.cutoffMode)) return "Booking Cutoff action is invalid";
            if (req.cutoffMode === "Custom" &&
                (req.cutoff === null || req.cutoff < 1 || req.cutoff > 365)) {
                return "Custom Booking Cutoff must be a whole number from 1 to 365";
            }

            const largestMin = Math.max(req.minStayArrival || 0, req.minStayThrough || 0);
            if (req.maxStay !== null && req.maxStay > 0 && largestMin > 0 && req.maxStay < largestMin) {
                return "Maximum Stay cannot be lower than the selected Minimum Stay";
            }

            if (forSave) {
                const hasNumeric = req.minStayArrival !== null || req.minStayThrough !== null || req.maxStay !== null;
                const hasCutoff = req.cutoffMode !== "None";
                const hasStatus = req.closedToArrivalStatus !== "None" || req.closedToDepartureStatus !== "None" || req.sellingStatus !== "None";
                if (!hasNumeric && !hasCutoff && !hasStatus) return "Select at least one restriction to update";
            }

            return null;
        }

        function syncControls() {
            const clearAll = $("#chkClearAll").is(":checked");

            $(".numeric-card").each(function () {
                const switchId = $(this).data("switch");
                const inputId = $(this).data("input");
                const enabled = !clearAll && $("#" + switchId).is(":checked");
                $("#" + switchId).prop("disabled", clearAll);
                $("#" + inputId).prop("disabled", !enabled);
                $(this).toggleClass("disabled-card", clearAll || !enabled);
            });

            $("#ddlClosedToArrival,#ddlClosedToDeparture,#ddlSellingStatus").prop("disabled", clearAll);
            $(".status-card").toggleClass("disabled-card", clearAll);

            const cutoffMode = clearAll ? "Disabled" : ($("#ddlCutoffMode").val() || "None");
            const customCutoff = !clearAll && cutoffMode === "Custom";

            $("#ddlCutoffMode").prop("disabled", clearAll);
            $("#txtCutoff").prop("disabled", !customCutoff);
            $("#cutoffCustomField").toggleClass("d-none", !customCutoff);
            $(".cutoff-card").toggleClass("disabled-card", clearAll);
        }

        function selectAll($select) {
            const values = $select.find("option").map(function () { return this.value; }).get();
            $select.val(values).trigger("change");
        }

        function clearSelect($select) { $select.val(null).trigger("change"); }

        async function loadDropdowns() {
            const response = unwrapResponse(await apiPost("LoadInit", {}));
            if (!response) return;

            $("#ddlBuckets").empty();
            (response.buckets || []).forEach(x => $("#ddlBuckets").append(new Option(x.text, x.value)));

            $("#ddlPlans").empty();
            (response.plans || []).forEach(x => $("#ddlPlans").append(new Option(x.text, x.value)));

            $("#ddlRoomTypes").empty();
            (response.rooms || []).forEach(x => $("#ddlRoomTypes").append(new Option(x.text, x.value)));

            $("#ddlBuckets,#ddlPlans,#ddlRoomTypes").select2({
                placeholder: "Select...",
                width: "100%",
                closeOnSelect: false
            });
        }

        function schedulePreview() {
            clearTimeout(previewTimer);
            previewTimer = setTimeout(previewSummary, 250);
        }

        async function previewSummary() {
            const req = buildRequest();
            const validation = validateRequest(req, false);

            if (validation) {
                $("#tblSummary tbody").html(`<tr><td colspan="10" class="text-center text-muted">${escapeHtml(validation)}</td></tr>`);
                return;
            }

            const response = unwrapResponse(await apiPost("Preview", { req: req }));
            if (!response) return;
            if (response.ok === false) {
                $("#tblSummary tbody").html(`<tr><td colspan="10" class="text-center text-danger">${escapeHtml(response.error || "Preview failed")}</td></tr>`);
                setStatus(response.error || "Preview failed", false);
                return;
            }

            const rows = Array.isArray(response.rows) ? response.rows : [];
            if (!rows.length) {
                $("#tblSummary tbody").html('<tr><td colspan="10" class="text-center text-muted">No rows to preview.</td></tr>');
                return;
            }

            let html = "";
            rows.forEach(row => {
                html += `<tr>
                    <td>${escapeHtml(row.dateRangeText)}</td>
                    <td>${escapeHtml(row.roomTypeText)}</td>
                    <td>${escapeHtml(row.planText)}</td>
                    <td>${escapeHtml(row.minStayArrivalText)}</td>
                    <td>${escapeHtml(row.minStayThroughText)}</td>
                    <td>${escapeHtml(row.maxStayText)}</td>
                    <td>${escapeHtml(row.cutoffText)}</td>
                    <td>${escapeHtml(row.closedToArrivalText)}</td>
                    <td>${escapeHtml(row.closedToDepartureText)}</td>
                    <td>${escapeHtml(row.stopSellText)}</td>
                </tr>`;
            });

            $("#tblSummary tbody").html(html);
            setStatus("Preview ready", true);
        }

        async function saveAll() {
            if (isSaving) return;

            const req = buildRequest();
            const validation = validateRequest(req, true);
            if (validation) return setStatus(validation, false);

            isSaving = true;
            $("#btnSave").prop("disabled", true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');
            setStatus("Saving restrictions...", true);

            try {
                const response = unwrapResponse(await apiPost("SaveRestrictions", { req: req }));
                if (!response) return;

                if (response.ok) {
                    setStatus(response.message || "Saved successfully", true);
                    setTimeout(function () { location.reload(); }, 900);
                } else {
                    setStatus(response.message || "Save failed", false);
                }
            } finally {
                isSaving = false;
                $("#btnSave").prop("disabled", false).html('<i class="fa-solid fa-floppy-disk"></i> Save Restrictions');
            }
        }

        $(document).ready(async function () {
            $("#rangeCalOverlay,#rangeCalPopup").hide();
            addRangeRow();

            $("#btnAddRange").on("click", addRangeRow);
            $("#rangesArea").on("click", ".btnDelRange", function () {
                $(this).closest(".range-row").remove();
                if (!$("#rangesArea .range-row").length) addRangeRow();
                schedulePreview();
            });
            $("#rangesArea").on("change", ".dateRangeTxt,.hfStartDate,.hfEndDate", schedulePreview);

            $("#btnPlansPlus").on("click", function () { selectAll($("#ddlPlans")); });
            $("#btnPlansClear").on("click", function () { clearSelect($("#ddlPlans")); });
            $("#btnRoomsPlus").on("click", function () { selectAll($("#ddlRoomTypes")); });
            $("#btnRoomsClear").on("click", function () { clearSelect($("#ddlRoomTypes")); });

            $("#btnDaysAll").on("click", function () { $(".dayChk").prop("checked", true); schedulePreview(); });
            $("#btnDaysClear").on("click", function () { $(".dayChk").prop("checked", false); schedulePreview(); });

            $(document).on("change", ".dayChk,#ddlBuckets,#ddlPlans,#ddlRoomTypes", schedulePreview);
            $("#swMinStayArrival,#swMinStayThrough,#swMaxStay,#chkClearAll,#ddlCutoffMode").on("change", function () { syncControls(); schedulePreview(); });
            $("#txtMinStayArrival,#txtMinStayThrough,#txtMaxStay,#txtCutoff").on("input", schedulePreview);
            $("#ddlClosedToArrival,#ddlClosedToDeparture,#ddlSellingStatus").on("change", schedulePreview);

            $("#btnCancel").on("click", function () { location.reload(); });
            $("#btnSave").on("click", saveAll);

            try {
                syncControls();
                await loadDropdowns();
                await previewSummary();
            } catch (error) {
                console.error(error);
                setStatus("Page load failed", false);
            }
        });
    </script>
</asp:Content>
