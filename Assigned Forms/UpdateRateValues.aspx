<%@ Page Title="" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true"
    CodeBehind="UpdateRateValues.aspx.cs" Inherits="hotelsoftware.UpdateRateValues" %>

<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
    <!-- Bootstrap -->
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" />
    <!-- Select2 -->
    <link href="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/css/select2.min.css" rel="stylesheet" />
    <!-- FontAwesome -->
    <link href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css" rel="stylesheet" />

    <style>
        body { background:#f4f6fb; }
        .top-bar { height: 10px; background: linear-gradient(90deg,#2f6ce5,#14b8a6); }
        
        .card-wrap{
            background:#fff; border-radius:18px; padding:22px 22px 18px;
            box-shadow:0 10px 30px rgba(0,0,0,.08);
            margin:18px auto; 
        }

        .title-row{
            display:flex; align-items:center; gap:10px; font-weight:800;
            color:#1f2937; margin-bottom:14px; letter-spacing:.3px;
            font-size:16px;
        }
        .title-row i{ color:#111827; }

        .field-label{ font-weight:600; font-size:13px; color:#374151; margin:0px 0 0px; }

        /* Select2 styling */
        .select2-container--default .select2-selection--multiple{
            border:2px solid #1d355740 !important;
            border-radius:12px !important;
            min-height:32px;
            padding:2px 46px 2px 8px;
        }
        .select2-container--default .select2-selection__choice{
            background:#e9edf5 !important;
            border:0 !important;
            border-radius:10px !important;
            padding:3px 10px !important;
            margin-top:3px !important;
        }
        .select2-container--default .select2-selection__choice__remove{ margin-right:6px !important; }

        .action-buttons{ display:flex; gap:8px; justify-content:flex-end; align-items:center; }
        .btn-ico{
            width:32px; height:32px; border-radius:10px;
            display:inline-flex; align-items:center; justify-content:center;
            border:0; box-shadow: 0 8px 18px rgba(0,0,0,.08);
        }
        .btn-plus{ background:#16a34a; color:#fff; }
        .btn-x{ background:#e11d48; color:#fff; }

        /* Range rows */
        .range-row{
            display:grid;
            grid-template-columns: 140px 1fr 130px 1fr 44px;
            gap:0px; align-items:center;
            margin-top:12px;
            padding:10px 10px;
            border:1px solid #e5e7eb;
            border-radius:14px;
            background:#fff;
        }

        .badge-small{
            font-size:12px; font-weight:normal;
            padding:8px 12px; border-radius:0px;height:32px;
            background:#1d3557; color:#fff;
            display:inline-flex; align-items:center; justify-content:center;
            white-space:nowrap;
        }

        .range-date .input-group .form-control{
            border:2px solid #c6ccd5;
            border-right:0;
            border-radius:0px;
            height:32px;
        }
        .range-date .input-group .cal-btn{
            border:2px solid #c6ccd5;
            border-left:0;
            background:#f9a917;
            cursor:pointer;
            height:32px;
            display:flex;
            align-items:center;
            justify-content:center;
            font-size:18px;
            user-select:none;
        }

        .baseRate{
            border:2px solid #c6ccd5 !important;
            height:32px;
            border-radius:0px !important;
        }

        .pill{
            display:inline-flex; align-items:center; gap:6px;
            border-radius:12px; padding:6px 12px;
            font-size:13px; margin:4px 6px 0 0;
            background:#eef2ff;
            border:1px solid #dbeafe;
            white-space:nowrap;
        }
        .pill input { transform: translateY(1px); }

        .days-box{
            border:2px solid #c6ccd5; border-radius:12px; padding:5px;
            min-height:52px;
        }

        .summary-title{ margin-top:16px; font-weight:900; color:#111827; }
        table.summary{ border-radius:14px; overflow:hidden; }
        table.summary thead th{ background:#bfeeee; border:0; font-weight:900; color:#0f172a; }
        table.summary td, table.summary th{ border-color:#e5e7eb; }
        table.summary tbody tr:nth-child(even){ background:#f7fbff; }

        .note { font-size:12px; color:#6b7280; margin-top:10px; }

        .footer-actions{ display:flex; gap:10px; margin-top:14px; align-items:center; }
        .btn-save{background: #20b996;
    color: #fff;
    border: 0;
    font-size: 15px;
    border-radius: 12px;
    padding: 7px 12px;
    font-weight: normal; }
        .btn-cancel{ background:#ef4444;font-size: 15px; color:#fff; border:0; border-radius:12px;padding: 7px 12px; font-weight:normal; }
        #lblStatus{ transition: all .2s ease; }
        #lblStatus.bad { background:#ef4444 !important; }
        #lblStatus.ok  { background:#16a34a !important; }
        /* Responsive */
        @media(max-width: 992px){
            .range-row{ grid-template-columns: 1fr; }
        }

        /* ====== Calendar Z-INDEX Fix ====== */
        #calendarPopup{
            display:none;
            position: fixed !important;
            z-index: 999999 !important;
            top: 12% !important;
            left: 50% !important;
            transform: translateX(-50%) !important;
            width: 980px;
            max-width: calc(100vw - 24px);
            background: #fff;
            border-radius: 14px;
            box-shadow: 0 20px 60px rgba(0,0,0,.25);
        }
        #screenblur{
            display:none;
            position: fixed;
            inset: 0;
            background: rgba(0,0,0,.45);
            backdrop-filter: blur(4px);
            z-index: 999998 !important;
        }
        .select2-container { z-index: 9999; }
        .select2-dropdown  { z-index: 9999; }

        /* Avoid parent clipping */
        .card-wrap, .container, .row, .col, .col-md-12 { overflow: visible !important; }
    </style>
    <style>
  /* ========= RANGE CALENDAR (Screenshot Style) ========= */
  #rangeCalOverlay{
    display:none;
    position: fixed;
    inset: 0;
    background: rgba(0,0,0,.25);
    backdrop-filter: blur(2px);
    z-index: 999998;
  }

  #rangeCalPopup{
    display:none;
    position: fixed;
    top: 120px;
    left: 50%;
    transform: translateX(-50%);
    z-index: 999999;
    background: #fff;
    border-radius: 10px;
    box-shadow: 0 16px 40px rgba(0,0,0,.18);
    width: 860px;
    max-width: calc(100vw - 20px);
    padding: 14px 14px 10px;
    border: 1px solid #e6e6e6;
  }

  .rcal-header{
    display:flex;
    justify-content: space-between;
    align-items:center;
    gap: 14px;
    padding: 4px 6px 10px;
  }

  .rcal-month-title{
    display:flex;
    align-items:center;
    gap: 10px;
    font-weight: 700;
    font-size: 14px;
    color:#111827;
    min-width: 140px;
    justify-content:center;
  }

  .rcal-nav{
    display:flex;
    gap: 8px;
    align-items:center;
  }

  .rcal-btn{
    border: 1px solid #d1d5db;
    background: #fff;
    border-radius: 8px;
    width: 34px;
    height: 34px;
    display:flex;
    align-items:center;
    justify-content:center;
    cursor:pointer;
    user-select:none;
  }
  .rcal-btn:hover{ background:#f3f4f6; }

  .rcal-body{
    display:grid;
    grid-template-columns: 1fr 1fr;
    gap: 16px;
    padding: 0 6px;
  }

  .rcal-panel{
    border: 1px solid #e5e7eb;
    border-radius: 10px;
    padding: 10px;
    background:#fff;
  }

  .rcal-dow{
    display:grid;
    grid-template-columns: repeat(7, 1fr);
    gap: 6px;
    font-size: 12px;
    color:#6b7280;
    font-weight:700;
    padding: 4px 2px 10px;
    text-align:center;
  }

  .rcal-grid{
    display:grid;
    grid-template-columns: repeat(7, 1fr);
    gap: 6px;
  }

  .rcal-day{
    height: 34px;
    border-radius: 6px;
    display:flex;
    align-items:center;
    justify-content:center;
    cursor:pointer;
    user-select:none;
    font-weight:600;
    font-size: 12px;
    color:#111827;
  }

  .rcal-day.is-other{ color:#9ca3af; cursor:default; }
  .rcal-day:hover:not(.is-other){ background:#eef2ff; }

  /* Range colors like screenshot */
  .rcal-day.is-start,
  .rcal-day.is-end{
    background:#1d4ed8;  /* blue */
    color:#fff;
  }
  .rcal-day.is-inrange{
    background:#dbeafe;  /* light blue */
    color:#111827;
  }

  .rcal-footer{
    display:flex;
    justify-content:flex-end;
    align-items:center;
    gap: 10px;
    padding: 12px 6px 2px;
  }

  .rcal-rangeText{
    margin-right:auto;
    font-weight:700;
    font-size: 12px;
    color:#111827;
  }

  .rcal-action{
    border: 1px solid #d1d5db;
    background:#fff;
    border-radius: 8px;
    padding: 6px 12px;
    font-weight: 700;
    font-size: 12px;
    cursor:pointer;
  }
  .rcal-action:hover{ background:#f3f4f6; }

  .rcal-apply{
    border: 0;
    background:#06b6d4; /* teal like screenshot */
    color:#fff;
  }
  .rcal-apply:hover{ filter: brightness(.95); }

  @media (max-width: 900px){
    #rangeCalPopup{ top: 80px; }
    .rcal-body{ grid-template-columns: 1fr; }
  }
</style>

</asp:Content>

<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder2" runat="server">
    <style>
        .mt-4{
   margin-top:0px !important
}
        .mt-lg-3 {
         margin-top: 0px !important; 
    }
    </style>
    <div class="top-bar"></div>

    <asp:HiddenField ID="hdHotelId" runat="server" />
    <asp:HiddenField ID="hdUserId" runat="server" />
    <asp:HiddenField ID="hdUserName" runat="server" />

    <div class="card-wrap">
        <div class="title-row">
            <i class="fa-solid fa-pen-to-square"></i>
            <div>UPDATE RATE VALUES</div>
        </div>

        <!-- Rate Plans -->
        <div class="d-flex justify-content-between align-items-end">
            <div class="w-100 me-2">
                <div class="field-label">Rate Plans</div>
                <select id="ddlPlans" class="form-select" multiple="multiple" style="width:100%"></select>
            </div>
            <div class="action-buttons">
                <button type="button" class="btn-ico btn-plus" id="btnPlansPlus" title="Select All">
                    <i class="fa-solid fa-plus"></i>
                </button>
                <button type="button" class="btn-ico btn-x" id="btnPlansClear" title="Clear">
                    <i class="fa-solid fa-xmark"></i>
                </button>
            </div>
        </div>

        <!-- Room Types -->
        <div class="d-flex justify-content-between align-items-end mt-2">
            <div class="w-100 me-2">
                <div class="field-label">Room Types</div>
                <select id="ddlRoomTypes" class="form-select" multiple="multiple" style="width:100%"></select>
            </div>
            <div class="action-buttons">
                <button type="button" class="btn-ico btn-plus" id="btnRoomsPlus" title="Select All">
                    <i class="fa-solid fa-plus"></i>
                </button>
                <button type="button" class="btn-ico btn-x" id="btnRoomsClear" title="Clear">
                    <i class="fa-solid fa-xmark"></i>
                </button>
            </div>
        </div>

        <!-- Date Ranges -->
        <div class="mt-3">
            <div class="field-label">Date Ranges</div>
            <div class="d-flex align-items-center gap-2">
                <a class="fw-bold text-decoration-none" style="color:#06b6d4; cursor:pointer;font-size:12px;" id="btnAddRange">
                    <i class="fa-solid fa-plus"></i> Add Range
                </a>
            </div>

            <div id="rangesArea"></div>
        </div>

        <!-- Applicable To -->
        <div class="mt-3">
            <div class="field-label">Applicable To</div>
            <div class="days-box">
                <label class="pill"><input type="checkbox" class="dayChk" value="0" checked /> Sunday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="1" checked /> Monday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="2" checked /> Tuesday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="3" checked /> Wednesday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="4" checked /> Thursday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="5" checked /> Friday</label>
                <label class="pill"><input type="checkbox" class="dayChk" value="6" checked /> Saturday</label>
            </div>
        </div>

        <!-- Summary -->
        <div class="summary-title">UPDATE SUMMARY</div>

        <div class="table-responsive mt-2">
            <table class="table summary" id="tblSummary">
                <thead>
                    <tr>
                        <th style="width:35%">Date Range</th>
                        <th style="width:20%">Room Type</th>
                        <th style="width:20%">Rate Plan</th>
                        <th style="width:25%" class="text-end">Adjusted Rate</th>
                    </tr>
                </thead>
                <tbody>
                    <tr>
                        <td colspan="4" class="text-center text-muted">
                            Select plans, room types and add a range to preview...
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>

        <div class="note">
            <i class="fa-solid fa-triangle-exclamation"></i>
            All values are exclusive of any yield management changes. Please be aware changing a rate may cause a delay before further yield management rules are applied.
        </div>

        <div class="footer-actions">
            <button type="button" class="btn-save" id="btnSave">
                <i class="fa-solid fa-floppy-disk"></i> Save
            </button>
            <button type="button" class="btn-cancel" id="btnCancel">
                <i class="fa-solid fa-circle-xmark"></i> Cancel
            </button>

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


    <!-- Scripts -->
    <script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/js/select2.min.js"></script>

   <script>
       /* =========================================================
          RANGE CALENDAR (two months)
          Function to call: openRangeCalendarFor(txtId, hfStartId, hfEndId)
          ========================================================= */

       // active target fields
       let _rcalTxtId = null;
       let _rcalStartId = null;
       let _rcalEndId = null;

       // month shown on left calendar
       let _viewYear = (new Date()).getFullYear();
       let _viewMonth = (new Date()).getMonth(); // 0..11

       // selected range
       let _selStart = null;
       let _selEnd = null;

       const _MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

       function pad2(n) { return (n < 10 ? "0" : "") + n; }

       function isoToUK(iso) {
           if (!iso) return "";
           const p = iso.split("-");
           return `${p[2]}/${p[1]}/${p[0]}`;
       }

       function ukToIso(uk) {
           if (!uk) return "";
           const p = uk.split("/");
           if (p.length !== 3) return "";
           return `${p[2]}-${pad2(parseInt(p[1], 10))}-${pad2(parseInt(p[0], 10))}`;
       }

       function _toIso(d) { return d.getFullYear() + "-" + pad2(d.getMonth() + 1) + "-" + pad2(d.getDate()); }
       function _toUK(d) { return pad2(d.getDate()) + "/" + pad2(d.getMonth() + 1) + "/" + d.getFullYear(); }

       function _parseIso(s) {
           if (!s) return null;
           const p = s.split("-");
           if (p.length !== 3) return null;
           const dt = new Date(parseInt(p[0], 10), parseInt(p[1], 10) - 1, parseInt(p[2], 10));
           return isNaN(dt.getTime()) ? null : dt;
       }

       function _sameDay(a, b) {
           return a && b &&
               a.getFullYear() === b.getFullYear() &&
               a.getMonth() === b.getMonth() &&
               a.getDate() === b.getDate();
       }

       function _daysInMonth(y, m) { return new Date(y, m + 1, 0).getDate(); }

       function _isInRange(d) {
           if (!_selStart || !_selEnd) return false;
           const t = d.getTime();
           return t > _selStart.getTime() && t < _selEnd.getTime();
       }

       function _setRangeText() {
           const $t = $("#rcalRangeText");
           if (!_selStart && !_selEnd) return $t.text("Select date range");
           if (_selStart && !_selEnd) return $t.text(_toUK(_selStart) + " - ...");
           $t.text(_toUK(_selStart) + " - " + _toUK(_selEnd));
       }

       function _renderMonth(gridId, y, m) {
           const $grid = $("#" + gridId).empty();
           const first = new Date(y, m, 1);
           const firstDow = first.getDay();
           const dim = _daysInMonth(y, m);

           for (let i = 0; i < firstDow; i++) {
               $grid.append(`<div class="rcal-day is-other"></div>`);
           }

           for (let d = 1; d <= dim; d++) {
               const dt = new Date(y, m, d);
               let cls = "rcal-day";
               if (_sameDay(dt, _selStart)) cls += " is-start";
               else if (_sameDay(dt, _selEnd)) cls += " is-end";
               else if (_isInRange(dt)) cls += " is-inrange";
               $grid.append(`<div class="${cls}" data-iso="${_toIso(dt)}">${d}</div>`);
           }

           const total = firstDow + dim;
           const rem = total % 7;
           if (rem !== 0) {
               const add = 7 - rem;
               for (let i = 0; i < add; i++) {
                   $grid.append(`<div class="rcal-day is-other"></div>`);
               }
           }
       }

       function _renderCalendar() {
           $("#rcalLeftTitle").text(_MONTHS[_viewMonth] + " " + _viewYear);

           let ry = _viewYear, rm = _viewMonth + 1;
           if (rm > 11) { rm = 0; ry++; }
           $("#rcalRightTitle").text(_MONTHS[rm] + " " + ry);

           _renderMonth("rcalLeftGrid", _viewYear, _viewMonth);
           _renderMonth("rcalRightGrid", ry, rm);
           _setRangeText();
       }

       function _openCalendar() {
           // move popup + overlay to body (avoids overflow/position bugs)
           if ($("#rangeCalOverlay").parent()[0] !== document.body) $("#rangeCalOverlay").appendTo(document.body);
           if ($("#rangeCalPopup").parent()[0] !== document.body) $("#rangeCalPopup").appendTo(document.body);

           $("#rangeCalOverlay").show();
           $("#rangeCalPopup").show();
           _renderCalendar();
       }

       function _closeCalendar() {
           $("#rangeCalOverlay").hide();
           $("#rangeCalPopup").hide();
       }

       function _clearCalendar() {
           _selStart = null; _selEnd = null;
           _renderCalendar();
       }

       function _applyCalendar() {
           if (!_rcalTxtId || !_rcalStartId || !_rcalEndId) return;

           if (!_selStart || !_selEnd) {
               $("#rcalRangeText").text("Select start and end date");
               return;
           }

           const sIso = _toIso(_selStart);
           const eIso = _toIso(_selEnd);

           $("#" + _rcalStartId).val(sIso).trigger("change");
           $("#" + _rcalEndId).val(eIso).trigger("change");
           $("#" + _rcalTxtId).val(_toUK(_selStart) + " - " + _toUK(_selEnd)).trigger("change");

           _closeCalendar();
       }

       function _handleDayClick(iso) {
           const dt = _parseIso(iso);
           if (!dt) return;

           if (!_selStart && !_selEnd) {
               _selStart = dt; _selEnd = null;
               return _renderCalendar();
           }

           if (_selStart && !_selEnd) {
               if (dt.getTime() < _selStart.getTime()) {
                   _selEnd = _selStart;
                   _selStart = dt;
               } else {
                   _selEnd = dt;
               }
               return _renderCalendar();
           }

           _selStart = dt;
           _selEnd = null;
           _renderCalendar();
       }

       // ✅ NEW name (won't be overridden by Site.Master)
       window.openRangeCalendarFor = function (txtId, hfStartId, hfEndId) {
           _rcalTxtId = txtId;
           _rcalStartId = hfStartId;
           _rcalEndId = hfEndId;

           _selStart = _parseIso($("#" + hfStartId).val());
           _selEnd = _parseIso($("#" + hfEndId).val());

           _openCalendar();
       };

       // Calendar events
       $(document).on("click", "#rangeCalOverlay", _closeCalendar);
       $(document).on("keydown", function (e) { if (e.key === "Escape") _closeCalendar(); });

       $(document).on("click", "#rcalPrev", function () {
           _viewMonth--;
           if (_viewMonth < 0) { _viewMonth = 11; _viewYear--; }
           _renderCalendar();
       });

       $(document).on("click", "#rcalNext", function () {
           _viewMonth++;
           if (_viewMonth > 11) { _viewMonth = 0; _viewYear++; }
           _renderCalendar();
       });

       $(document).on("click", "#rcalClear", _clearCalendar);
       $(document).on("click", "#rcalApply", _applyCalendar);

       $(document).on("click",
           "#rcalLeftGrid .rcal-day:not(.is-other), #rcalRightGrid .rcal-day:not(.is-other)",
           function () { _handleDayClick($(this).attr("data-iso")); }
       );

       /* =========================================================
          MAIN PAGE JS (Preview/Save + select all/clear)
          ========================================================= */

       let rangeIndex = 0;

       function setStatus(text, good) {
           $("#lblStatus").text(text);
           $("#lblStatus").toggleClass("ok", !!good).toggleClass("bad", !good);
       }

       async function apiPost(method, data) {
           const url = "UpdateRateValues.aspx/" + method;
           return $.ajax({
               url: url,
               type: "POST",
               contentType: "application/json; charset=utf-8",
               dataType: "json",
               data: JSON.stringify(data || {}),
               timeout: 30000
           }).fail(function (xhr) {
               console.log("AJAX FAIL:", url, xhr.status, xhr.responseText);
               let msg = "AJAX failed (" + xhr.status + ")";
               try {
                   const j = JSON.parse(xhr.responseText);
                   if (j && j.Message) msg = j.Message;
               } catch { }
               setStatus(msg, false);
           });
       }

       function addRangeRow() {
           rangeIndex++;

           const txtId = "txt_dateRange_" + rangeIndex;
           const hfStartId = "hfStartDate_" + rangeIndex;
           const hfEndId = "hfEndDate_" + rangeIndex;

           const html = `
      <div class="range-row">
        <div class="badge-small">Date Range</div>

        <div class="range-date" style="height:32px;">
          <div class="input-group">
            <input type="text" id="${txtId}" class="form-control dateRangeTxt" readonly
                   placeholder="dd/mm/yyyy - dd/mm/yyyy" />
            <input type="hidden" id="${hfStartId}" class="hfStartDate" />
            <input type="hidden" id="${hfEndId}" class="hfEndDate" />
            <span class="input-group-text cal-btn"
                  title="Select date range"
                  onclick="openRangeCalendarFor('${txtId}', '${hfStartId}', '${hfEndId}')">📅</span>
          </div>
          <div class="small text-muted d-none mt-1"><span class="rangeText"></span></div>
        </div>

        <div class="badge-small">Base Rate</div>

        <div>
          <input type="number" class="form-control baseRate" value="0" min="0" step="0.01" />
        </div>

        <button type="button" class="btn-ico btn-x btnDelRange" title="Delete">
          <i class="fa-solid fa-trash"></i>
        </button>
      </div>`;
           $("#rangesArea").append(html);
       }

       function refreshRangeText() {
           $("#rangesArea .range-row").each(function () {
               const sIso = $(this).find(".hfStartDate").val();
               const eIso = $(this).find(".hfEndDate").val();
               const txt = (sIso && eIso) ? (isoToUK(sIso) + " - " + isoToUK(eIso)) : "";
               $(this).find(".rangeText").text(txt);

               const $txt = $(this).find(".dateRangeTxt");
               if (txt && (!$txt.val() || $txt.val().trim() === "")) $txt.val(txt);
           });
       }

       function getSelectedDays() {
           const days = [];
           $(".dayChk:checked").each(function () { days.push(parseInt($(this).val(), 10)); });
           return days;
       }

       function getRangesPayload() {
           const ranges = [];
           $("#rangesArea .range-row").each(function () {
               let s = $(this).find(".hfStartDate").val();
               let e = $(this).find(".hfEndDate").val();

               if ((!s || !e)) {
                   const v = ($(this).find(".dateRangeTxt").val() || "").trim();
                   const parts = v.split("-");
                   if (parts.length === 2) {
                       s = ukToIso(parts[0].trim());
                       e = ukToIso(parts[1].trim());
                   }
               }

               const b = $(this).find(".baseRate").val();
               ranges.push({ start: s, end: e, baseRate: parseFloat(b || "0") });
           });
           return ranges;
       }

       function selectAll($sel) {
           const allValues = $sel.find("option").map(function () { return this.value; }).get();
           $sel.val(allValues).trigger("change");
       }

       function clearAll($sel) { $sel.val(null).trigger("change"); }

       async function loadDropdowns() {
           const res = await apiPost("LoadInit", {});
           if (!res || !res.d) return;

           const data = res.d;

           $("#ddlPlans").empty();
           (data.plans || []).forEach(p => $("#ddlPlans").append(new Option(p.text, p.value)));

           $("#ddlRoomTypes").empty();
           (data.rooms || []).forEach(r => $("#ddlRoomTypes").append(new Option(r.text, r.value)));

           $("#ddlPlans, #ddlRoomTypes").select2({
               placeholder: "Select...",
               width: "100%",
               closeOnSelect: false
           });
       }

       async function previewSummary() {
           refreshRangeText();

           const plans = $("#ddlPlans").val() || [];
           const rooms = $("#ddlRoomTypes").val() || [];
           const ranges = getRangesPayload();
           const days = getSelectedDays();

           if (plans.length === 0 || rooms.length === 0 || ranges.length === 0) {
               $("#tblSummary tbody").html(
                   `<tr><td colspan="4" class="text-center text-muted">
        Select plans, room types and add a range to preview...
      </td></tr>`
               );
               return;
           }

           for (const rg of ranges) {
               if (!rg.start || !rg.end) {
                   $("#tblSummary tbody").html(
                       `<tr><td colspan="4" class="text-danger text-center">
          Please select a date range (use 📅)
        </td></tr>`
                   );
                   return;
               }
           }

           const payload = { plans, rooms, ranges, days };
           const res = await apiPost("Preview", { req: payload });
           if (!res) return;

           // ✅ Normalize rows from ASP.NET response
           let d = res.d;

           // if server returned stringified JSON
           if (typeof d === "string") {
               try { d = JSON.parse(d); } catch { /* keep as string */ }
           }

           // possible formats:
           // 1) d = [ ... ]
           // 2) d = { rows:[...], ok:true }
           // 3) d = { d:[...] } (rare)
           // 4) d = null
           let rows = null;

           if (Array.isArray(d)) {
               rows = d;
           } else if (d && Array.isArray(d.rows)) {
               rows = d.rows;
           } else if (d && Array.isArray(d.data)) {
               rows = d.data;
           } else if (d && Array.isArray(d.items)) {
               rows = d.items;
           } else {
               rows = null;
           }

           if (!rows || rows.length === 0) {
               console.log("Preview response (not array):", res.d);
               $("#tblSummary tbody").html(
                   `<tr><td colspan="4" class="text-center text-muted">
        No rows (check dates/days)
      </td></tr>`
               );
               setStatus("No rows", true);
               return;
           }

           let html = "";
           rows.forEach(r => {
               html += `
      <tr>
        <td>${r.dateRangeText || ""}</td>
        <td>${r.roomTypeText || ""}</td>
        <td>${r.planText || ""}</td>
        <td class="text-end">${("")}${parseFloat(r.adjustedRate || 0).toFixed(2)}</td>
      </tr>`;
           });

           $("#tblSummary tbody").html(html);
           setStatus("Preview ready", true);
       }
       let activeRateSaveJobId = null;
       let rateSavePollTimer = null;

       async function saveAll() {
           if (activeRateSaveJobId) {
               setStatus("Rate saving is already running", false);
               return;
           }

           const plans = $("#ddlPlans").val() || [];
           const rooms = $("#ddlRoomTypes").val() || [];
           const ranges = getRangesPayload();
           const days = getSelectedDays();

           if (plans.length === 0) {
               setStatus("Select Rate Plans", false);
               return;
           }

           if (rooms.length === 0) {
               setStatus("Select Room Types", false);
               return;
           }

           if (ranges.length === 0) {
               setStatus("Add Date Range", false);
               return;
           }

           for (const rg of ranges) {
               if (!rg.start || !rg.end) {
                   setStatus("Select date range using calendar", false);
                   return;
               }

               if (new Date(rg.end) < new Date(rg.start)) {
                   setStatus("End date must be greater than or equal to start date", false);
                   return;
               }

               if (parseFloat(rg.baseRate || 0) < 0) {
                   setStatus("Base rate cannot be negative", false);
                   return;
               }
           }

           const payload = {
               plans: plans,
               rooms: rooms,
               ranges: ranges,
               days: days
           };

           setSaveButtonWorking(true);
           setStatus("Starting background save...", true);

           $.ajax({
               url: "UpdateRateValues.aspx/StartSaveRates",
               type: "POST",
               contentType: "application/json; charset=utf-8",
               dataType: "json",
               data: JSON.stringify({
                   req: payload
               }),
               timeout: 60000,
               cache: false
           })
               .done(async function (response)  {
                   const result = response && response.d
                       ? response.d
                       : null;

                   if (!result) {
                       setSaveButtonWorking(false);
                       setStatus("Server returned an empty response", false);
                       return;
                   }

                   if (!result.ok) {
                       setSaveButtonWorking(false);
                       setStatus(
                           result.message || "Unable to start saving",
                           false
                       );
                       return;
                   }

                   activeRateSaveJobId = result.jobId;

                   setStatus(
                       "Saving started in background...",
                       true
                   );

                   checkRateSaveStatus();
               })
               .fail(function (xhr, textStatus, errorThrown) {
                   setSaveButtonWorking(false);

                   let message = "Unable to start background saving";

                   try {
                       if (
                           xhr.responseJSON &&
                           xhr.responseJSON.Message
                       ) {
                           message = xhr.responseJSON.Message;
                       } else if (xhr.responseText) {
                           const parsed = JSON.parse(xhr.responseText);

                           if (parsed.Message) {
                               message = parsed.Message;
                           }
                       }
                   } catch (ignore) {
                   }

                   if (xhr.status === 0) {
                       message =
                           "Connection interrupted while starting the save job.";
                   } else if (textStatus === "timeout") {
                       message =
                           "The server took too long to start the save job.";
                   }

                   console.error(
                       "StartSaveRates failed:",
                       xhr.status,
                       textStatus,
                       errorThrown,
                       xhr.responseText
                   );

                   setStatus(message, false);
               });
       }

       function checkRateSaveStatus() {
           if (!activeRateSaveJobId) {
               return;
           }

           $.ajax({
               url: "UpdateRateValues.aspx/GetSaveRatesStatus",
               type: "POST",
               contentType: "application/json; charset=utf-8",
               dataType: "json",
               data: JSON.stringify({
                   jobId: activeRateSaveJobId
               }),
               timeout: 30000,
               cache: false
           })
               .done(async function (response) {
                   const result = response && response.d
                       ? response.d
                       : null;

                   if (!result) {
                       setStatus(
                           "Waiting for saving status...",
                           true
                       );

                       scheduleNextRateSaveStatusCheck(5000);
                       return;
                   }

                   if (!result.ok) {
                       if (
                           result.status === "not_found" ||
                           result.status === "unauthorized" ||
                           result.status === "failed" ||
                           result.status === "cancelled"
                       ) {
                           completeRateSaveJob(
                               result.message ||
                               "Unable to check rate-saving status.",
                               false
                           );

                           return;
                       }

                       scheduleNextRateSaveStatusCheck(5000);
                       return;
                   }

                   if (result.message) {
                       setStatus(
                           result.message,
                           result.status !== "failed"
                       );
                   }

                   if (result.status === "completed") {
                       completeRateSaveJob(
                           result.message ||
                           "Rate saving completed successfully.",
                           true
                       );

                       return;
                   }

                   if (result.status === "completed_with_warning") {
                       if (rateSavePollTimer) {
                           window.clearTimeout(rateSavePollTimer);
                           rateSavePollTimer = null;
                       }

                       activeRateSaveJobId = null;
                       setSaveButtonWorking(false);

                       setStatus(
                           result.message ||
                           "Rates saved with a channel-upload warning.",
                           false
                       );

                       try {
                           await previewSummary();
                       } catch (error) {
                           console.error(
                               "Preview refresh failed:",
                               error
                           );
                       }

                       alert(
                           result.message ||
                           "Rates were saved, but channel upload was not completed."
                       );

                       return;
                   }

                   if (
                       result.status === "failed" ||
                       result.status === "cancelled" ||
                       result.status === "not_found" ||
                       result.status === "unauthorized"
                   ) {
                       completeRateSaveJob(
                           result.message ||
                           "Rate saving failed.",
                           false
                       );

                       return;
                   }

                   scheduleNextRateSaveStatusCheck(2000);
               })
               .fail(function (xhr) {
                   console.log(
                       "Save status check failed:",
                       xhr.status,
                       xhr.responseText
                   );

                   setStatus(
                       "Saving is still running. Checking again...",
                       true
                   );

                   scheduleNextRateSaveStatusCheck(5000);
               });
       }

       function scheduleNextRateSaveStatusCheck(delay) {
           if (rateSavePollTimer) {
               window.clearTimeout(rateSavePollTimer);
           }

           rateSavePollTimer = window.setTimeout(
               checkRateSaveStatus,
               delay
           );
       }

       async function completeRateSaveJob(message, success) {
           if (rateSavePollTimer) {
               window.clearTimeout(rateSavePollTimer);
               rateSavePollTimer = null;
           }

           activeRateSaveJobId = null;

           setSaveButtonWorking(false);
           setStatus(message, success);

           if (success) {
               try {
                   await previewSummary();
               } catch (error) {
                   console.error(
                       "Preview refresh failed:",
                       error
                   );
               }

               alert(message);
           } else {
               alert(message);
           }
       }

       function setSaveButtonWorking(isWorking) {
           const $button = $("#btnSave");

           if (isWorking) {
               if (!$button.data("original-html")) {
                   $button.data(
                       "original-html",
                       $button.html()
                   );
               }

               $button
                   .prop("disabled", true)
                   .html(
                       '<i class="fa-solid fa-spinner fa-spin"></i> Saving...'
                   );

               $("#btnCancel").prop("disabled", true);
           } else {
               const originalHtml =
                   $button.data("original-html");

               $button
                   .prop("disabled", false)
                   .html(
                       originalHtml ||
                       '<i class="fa-solid fa-floppy-disk"></i> Save'
                   );

               $("#btnCancel").prop("disabled", false);
           }
       }

       //async function saveAll() {
       //    const plans = $("#ddlPlans").val() || [];
       //    const rooms = $("#ddlRoomTypes").val() || [];
       //    const ranges = getRangesPayload();
       //    const days = getSelectedDays();

       //    if (plans.length === 0) return setStatus("Select Rate Plans", false);
       //    if (rooms.length === 0) return setStatus("Select Room Types", false);
       //    if (ranges.length === 0) return setStatus("Add Date Range", false);

       //    for (const rg of ranges) {
       //        if (!rg.start || !rg.end) return setStatus("Select date range using 📅", false);
       //        if (new Date(rg.end) < new Date(rg.start)) return setStatus("End date must be >= start date", false);
       //    }

       //    setStatus("Saving...", true);

       //    const payload = { plans, rooms, ranges, days };
       //    const res = await apiPost("SaveRates", { req: payload });
       //    if (!res || !res.d) return;

       //    if (res.d.ok) {
       //        setStatus("Saved Successfully", true);
       //        await previewSummary();
       //    } else {
       //        setStatus(res.d.message || "Save failed", false);
       //    }
       //}

       $(document).ready(async function () {
           $("#rangeCalOverlay").hide();
           $("#rangeCalPopup").hide();

           addRangeRow();

           $("#btnAddRange").on("click", function () { addRangeRow(); });

           $("#rangesArea").on("click", ".btnDelRange", function () {
               $(this).closest(".range-row").remove();
               previewSummary();
           });

           $("#rangesArea").on("input change keyup", ".baseRate", function () { previewSummary(); });

           $("#rangesArea").on("change keyup blur", ".dateRangeTxt,.hfStartDate,.hfEndDate", function () {
               previewSummary();
           });

           $(document).on("change", ".dayChk", previewSummary);
           $("#ddlPlans, #ddlRoomTypes").on("change", previewSummary);

           $("#btnPlansPlus").on("click", function () { selectAll($("#ddlPlans")); });
           $("#btnRoomsPlus").on("click", function () { selectAll($("#ddlRoomTypes")); });

           $("#btnPlansClear").on("click", function () { clearAll($("#ddlPlans")); });
           $("#btnRoomsClear").on("click", function () { clearAll($("#ddlRoomTypes")); });

           $("#btnCancel").on("click", function () { location.reload(); });
           $("#btnSave").on("click", saveAll);

           try {
               await loadDropdowns();
               await previewSummary();
           } catch (e) {
               console.error(e);
               setStatus("Load failed", false);
           }
       });
   </script>



</asp:Content>
