  <%@ Page MasterPageFile="Site.Master" EnableEventValidation="false" Language="C#" AutoEventWireup="true" Inherits="hotelsoftware.AvailabilitySetup" Codebehind="AvailabilitySetup.aspx.cs" %>
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder2" runat="server">
    <asp:HiddenField runat="server" ID="hdnbaseurl" />
        <asp:HiddenField ID="hfHotelIdB64" runat="server" />
<asp:HiddenField ID="hfUserIdB64" runat="server" />
    <input type="hidden" id="hfHotelBaseRate" value="<%= _hotelBaseRateClient %>" />
    <asp:UpdatePanel runat="server" ID="updatepanel" UpdateMode="Conditional">
        <ContentTemplate>
 <style> 
    .containerbox {
        display: flex;
        align-items: center;
        gap: 5px;  
    }
    .box {
        width: 15px;
        height: 15px;
        text-align: center;
        line-height: 30px;
        font-weight: bold;
    }
    .uploaded {
        background-color: #ebffecc2;
        border: 2px solid #ccc;
    }
    .save-unuploaded {
        background-color: #fff7d0;  
    }
    .unuploaded {
        background-color: #ffaaaa;
        color: white;
        border: 2px solid #fea1a1;
    }
    .text {
        font-size: 11px;
        font-weight:bold;
    }
   .tablepop 
    {
        border-collapse: collapse;
        width: 100%;
    }
   .tablepop td 
    {
        border: 1px solid LightGray;
        padding: 3px;
        text-align: left;
    }
   .tablepop th 
    {
        border: 1px solid LightGray;
        padding:2px;
    }
   #Table4 td:nth-child(1)
    {
        position: sticky;
        background-color: #ffffff;
        left:0;
        z-index: 1;  
    }
   #Table4 {
        width: 100%;
        border-collapse: collapse;
        border: 1px solid lightblue;
    }
   #Table4 td {
       background-color: #ffffff;
        text-align: center;
        padding:5px 7px;
         border: 1px solid lightblue;
    }

    #Table4 td.effective-stop-sell,
    #Table4 td.effective-stop-sell .calInput {
        background-color: #ffccc7 !important;
    }

   /* Highlight only Saturday/Sunday date headers.
      Body cells keep all existing dynamic colours unchanged. */
   #Table4 tr:first-child td.weekend-column {
       background-color: #a8e27c !important;
       color: #8a6200 !important;
       font-weight: 700;
       /*box-shadow: inset 0 -3px 0 #e0a800;*/
   }
   #container {
        display: flex;
        flex-direction: column;
        overflow: hidden;
    }
   #scrolldiv {
        position: relative;
        z-index: 1;
    }
   table, tr, td{
       border:1px solid #dddddd;
   }
   .rowname {
    /* flex-shrink: 0; */
    width: 100%;
    max-width: 100%;
    padding-right: calc(var(--bs-gutter-x)* .5);
    padding-left: calc(var(--bs-gutter-x)* .5);
    margin-top: var(--bs-gutter-y);
}
   .screenblur {
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background-color: rgba(0, 0, 0, 0.5); /* Semi-transparent black */
    backdrop-filter: blur(5px); /* Apply blur effect */
    z-index: 998; /* Ensure overlay is behind the popup */
} 
</style>       
<style>
.rowstyle {
    flex-shrink: 0;
    width: 100%;
    max-width: 100%;
    margin-top: var(--bs-gutter-y);
    padding-right: calc(var(--bs-gutter-x)* 0);
}
.container {
    position: relative;
    display: inline-block;
}
.tooltip {
    visibility: hidden;
    width: 250px;
    height:auto;
    background-color: #333;
    color: #fff;
    text-align: center;
    border-radius: 6px;
    padding: 5px 10px;
    position: absolute;
    z-index: 1;
    top:10%;
    left:20%;
    opacity: 0; 
    transition: opacity 0.3s, visibility 0.3s;
}
.page-header{
    font-size:14px;
}
.page-header:hover .tooltip {
    visibility: visible;
    opacity: 1;
    
}
.tooltip2 {
    visibility: hidden;
    width: 400px;
    height:auto;
    background-color: #333;
    color: #fff;
    text-align: center;
    border-radius: 6px;
    padding: 5px 10px;
    position: absolute;
    z-index: 1;
    top:10%;
    left:40%;
    opacity: 0; /* Default hidden state */
    transition: opacity 0.3s, visibility 0.3s;
}
.updatelabel:hover .tooltip2 {
    visibility: visible;
    opacity: 1;
}
.toggle-btn {
    padding: 3px 15px;
    font-size: 14px;
    cursor: pointer;
    border: 2px solid lightgrey;
    background-color: white; 
    color: black;
    border-radius: 0;
    transition: all 0.3s ease-in-out;
    margin: 0;
    outline: none;
}
.toggle-btn.active {
    background-color: #d3d3d3; 
    font-weight: bold;
}
.left-btn {
    border-top-left-radius: 10px;
    border-bottom-left-radius: 10px;
}
.right-btn {
    border-top-right-radius: 10px;
    border-bottom-right-radius: 10px;
} 
 </style>
<style>
    .container {
        max-width: 400px;
        margin: 0 auto;
        background: white;
        padding: 20px;
        border-radius: 8px;
        box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    }

    .dropdown-container {
        position: relative;
    }

    .dropdown-field {
        min-height: 31px;
        border: 1px solid #ccc;
        padding: 3px 5px;
        background: white;
        cursor: pointer;
        position: relative;
    }

    .dropdown-field:hover {
        border-color: #999;
    }

    .selected-tags {
        display: flex;
        flex-wrap: wrap;
        gap: 5px;

    }

    .tag {
        display: inline-flex;
        align-items: center;
        background: #e0e0e0;
        color: #333;
        padding: 3px 6px;
        border-radius: 12px;
        font-size: 10px;
        font-weight: 500;
    }

    .tag-remove {
        margin-left: 6px;
        cursor: pointer;
        font-weight: bold;
        font-size: 14px;
        line-height: 1;
        padding: 0 2px;
        border-radius: 50%;
    }

    .tag-remove:hover {
        background: rgba(0,0,0,0.2);
    }

    .dropdown-arrow {
        position: absolute;
        right: 10px;
        top: 50%;
        transform: translateY(-50%);
        color: #666;
        transition: transform 0.2s;
    }

    .dropdown-arrow.open {
        transform: translateY(-50%) rotate(180deg);
    }

    .dropdown-menu {
        position: absolute;
        top: 100%;
        left: 0;
        right: 0;
        background: white;
        border: 1px solid #ccc;
        border-top: none;
        border-radius: 0 0 4px 4px;
        max-height: 200px;
        overflow-y: auto;
        z-index: 1000;
        display: none;
    }

    .dropdown-menu.show {
        display: block;
    }

    .dropdown-item {
        padding: 10px 15px;
        cursor: pointer;
        display: flex;
        justify-content: space-between;
        align-items: center;
    }

    .dropdown-item:hover {
        background: #f0f0f0;
    }

    .dropdown-item.selected {
        background: #e3f2fd;
        color: #1976d2;
    }

    .dropdown-item.disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }

    .dropdown-item.disabled:hover {
        background: white;
    }

    .selected-indicator {
        width: 8px;
        height: 8px;
        background: #2196F3;
        border-radius: 50%;
    }

    .save-btn {
        width: 100%;
        padding: 10px;
        background: #2196F3;
        color: white;
        border: none;
        border-radius: 4px;
        cursor: pointer;
        font-size: 14px;
        font-weight: 500;
    }

    .save-btn:hover {
        background: #1976d2;
    }

    .save-btn:disabled {
        background: #ccc;
        cursor: not-allowed;
    }

    .saved-data {
        margin-top: 20px;
        padding: 15px;
        background: #f9f9f9;
        border-radius: 4px;
        border-left: 4px solid #2196F3;
    }

    .saved-data h3 {
        margin: 0 0 10px 0;
        font-size: 14px;
        color: #333;
    }

    .saved-item {
        font-size: 12px;
        color: #666;
        margin-bottom: 5px;
    }
    .loading-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: rgba(0,0,0,0.5);
    z-index: 9999;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-direction: column;
}

.loading-spinner {
    width: 60px;
    height: 60px;
    border: 8px solid #f3f3f3;
    border-top: 8px solid #20b996; /* same green as header */
    border-radius: 50%;
    -webkit-animation: spin 1s linear infinite;
    animation: spin 1s linear infinite;
}

@-webkit-keyframes spin {
    0%   { -webkit-transform: rotate(0deg); }
    100% { -webkit-transform: rotate(360deg); }
}
@keyframes spin {
    0%   { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}
.btn-auto-availability {
    background: linear-gradient(135deg, #16a085, #1abc9c);
    border: none;
    color: #ffffff;
    font-size: 12px;
    font-weight: 600;
    letter-spacing: 0.4px;
    padding: 6px 18px;
    border-radius: 20px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    box-shadow: 0 3px 8px rgba(0,0,0,0.18);
    transition: all 0.2s ease-in-out;
    text-transform: uppercase;
    white-space: nowrap;
}

.btn-auto-availability:hover,
.btn-auto-availability:focus {
    transform: translateY(-1px);
    box-shadow: 0 5px 14px rgba(0,0,0,0.25);
    background: linear-gradient(135deg, #149174, #18b28a);
    outline: none;
}

.btn-auto-availability:active {
    transform: translateY(0);
    box-shadow: 0 2px 6px rgba(0,0,0,0.2);
}

.btn-auto-availability[disabled],
.btn-auto-availability.disabled {
    opacity: 0.6;
    cursor: not-allowed;
    box-shadow: none;
}

.btn-save-edited-rates {
    border:none;
    border-radius:20px;
    padding:6px 18px;
    background:#dc2626;
    color:#fff;
    font-size:12px;
    font-weight:700;
    letter-spacing:.2px;
    white-space:nowrap;
    box-shadow:0 3px 8px rgba(0,0,0,.14);
}

.btn-save-edited-rates:hover,
.btn-save-edited-rates:focus {
    background:#b91c1c;
    color:#fff;
    outline:none;
}

.btn-save-edited-rates:disabled {
    opacity:.55;
    cursor:not-allowed;
    box-shadow:none;
}


</style>
<style>
    /* ===== 30-day inventory navigator ===== */
    .inventory-topbar {
        display:flex;
        align-items:center;
        justify-content:space-between;
        gap:14px;
        flex-wrap:nowrap;
        min-height:48px;
        margin:0 0 8px;
        padding:9px 12px;
        background:#eef4fb;
        border-bottom:1px solid #dfe8f2;
    }

    .inventory-title-wrap {
        display:flex;
        align-items:center;
        gap:18px;
        flex-wrap:wrap;
        min-width:280px;
        flex:1 1 auto;
    }

    .inventory-title {
        display:flex;
        align-items:center;
        gap:6px;
    }

    .inventory-legend {
        display:flex;
        align-items:center;
        gap:16px;
        flex-wrap:wrap;
    }

    .inventory-controls-row {
        display:flex;
        align-items:center;
        gap:12px;
        width:100%;
        margin:0;
        padding:0;
        background:#fff;
    }

    .inventory-toolbar-nav-wrap {
        margin-left:auto;
        display:flex;
        align-items:center;
        justify-content:flex-end;
        min-width:0;
    }

    .inventory-nav {
        margin-left:auto;
        display:flex;
        align-items:center;
        justify-content:flex-end;
        gap:8px;
        flex-wrap:wrap;
    }

    .inventory-nav-btn {
        width:36px;
        height:36px;
        min-width:36px;
        padding:0;
        border:0;
        border-radius:50%;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        background:#f5a623;
        color:#fff !important;
        text-decoration:none !important;
        font-size:22px;
        font-weight:700;
        line-height:1;
        box-shadow:0 1px 3px rgba(0,0,0,.12);
        transition:transform .15s ease, background .15s ease;
    }

    .inventory-nav-btn:hover,
    .inventory-nav-btn:focus {
        background:#e99a16;
        color:#fff !important;
        transform:translateY(-1px);
        outline:none;
    }

    .inventory-current-date {
        height:38px;
        min-width:150px;
        padding:0 13px;
        border:1px solid #6ea8ca;
        border-radius:0;
        background:#fff;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        font-size:13px;
        font-weight:600;
        color:#27313a;
        white-space:nowrap;
    }

    .inventory-today-btn {
        height:38px;
        padding:0 12px;
        border:1px solid #5d9fc7;
        border-left:0;
        margin-left:-8px;
        background:#5d9fc7;
        color:#fff !important;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        text-align:center;
        text-decoration:none !important;
        font-size:11px;
        font-weight:700;
        line-height:1.05;
        white-space:nowrap;
    }

    .inventory-today-btn:hover,
    .inventory-today-btn:focus {
        background:#4d8db4;
        color:#fff !important;
        outline:none;
    }

    .inventory-date-controls {
        margin-left:auto;
        display:flex;
        align-items:center;
        justify-content:flex-end;
        gap:7px;
        flex:0 0 auto;
        white-space:nowrap;
    }

    .inventory-date-controls .inventory-date-label {
        margin:0;
        font-size:11px;
        font-weight:700;
        color:#364152;
    }

    .inventory-date-controls .inventory-date-input {
        width:132px;
        height:34px;
        padding:4px 8px;
        border:1px solid #cbd5e1;
        border-radius:3px;
        background:#fff;
        color:#27313a;
        font-size:11px;
    }

    .inventory-date-controls .inventory-date-search {
        width:38px;
        height:34px;
        padding:0;
        border:0;
        border-radius:3px;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        background:#457b9d;
        color:#fff !important;
        text-decoration:none !important;
        font-size:15px;
    }

    .inventory-date-controls .inventory-date-search:hover,
    .inventory-date-controls .inventory-date-search:focus {
        background:#386b8a;
        color:#fff !important;
        outline:none;
    }


    /* Existing server From/To controls remain in the page but are not shown directly. */
    .inventory-hidden-date-controls {
        display:none !important;
    }

    /* Payment-report style date-range selector placed between previous/next arrows. */
    .inventory-range-picker {
        position:relative;
        display:flex;
        align-items:center;
        width:238px;
        min-width:190px;
        height:38px;
        flex:0 1 238px;
    }

    .inventory-range-input {
        width:100%;
        height:38px;
        margin:0;
        padding:0 44px 0 11px;
        border:1px solid #6ea8ca;
        border-radius:0;
        background:#fff;
        color:#27313a;
        font-size:11px;
        font-weight:600;
        line-height:38px;
        white-space:nowrap;
        cursor:pointer;
        box-sizing:border-box;
    }

    .inventory-range-input:focus {
        border-color:#f5a623;
        outline:none;
        box-shadow:0 0 0 2px rgba(245,166,35,.12);
    }

    .inventory-calendar-btn {
        position:absolute;
        top:0;
        right:0;
        width:40px;
        height:38px;
        padding:0;
        border:0;
        border-left:1px solid #ead7b2;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        background:#f5a623;
        color:#fff;
        cursor:pointer;
        z-index:3;
        font-size:15px;
    }

    .inventory-calendar-btn:hover,
    .inventory-calendar-btn:focus {
        background:#e99a16;
        color:#fff;
        outline:none;
    }

    .inventory-range-search {
        width:38px;
        height:38px;
        min-width:38px;
        padding:0;
        border:0;
        border-radius:0;
        display:inline-flex;
        align-items:center;
        justify-content:center;
        background:#457b9d;
        color:#fff !important;
        text-decoration:none !important;
        font-size:15px;
        flex:0 0 38px;
    }

    .inventory-range-search:hover,
    .inventory-range-search:focus {
        background:#386b8a;
        color:#fff !important;
        outline:none;
    }

    /* =========================================================
       Inventory date range calendar position fix
       Keep the two-month popup fully inside the browser viewport.
       The range selector sits near the right side of the toolbar,
       so the popup is anchored from its right edge instead of left.
       ========================================================= */
    .inventory-range-picker .calendar-popup {
        position:absolute !important;
        top:calc(100% + 6px) !important;
        left:auto !important;
        right:0 !important;
        z-index:10050 !important;
        max-width:calc(100vw - 24px) !important;
        box-sizing:border-box;
    }

    /* Keep the popup inside the viewport on medium/tablet screens too. */
    @media (max-width: 1200px) {
        .inventory-range-picker .calendar-popup {
            left:auto !important;
            right:0 !important;
            max-width:calc(100vw - 20px) !important;
        }
    }

    /* On phones use the viewport itself instead of the small picker as anchor. */
    @media (max-width: 767px) {
        .inventory-range-picker .calendar-popup {
            position:fixed !important;
            top:70px !important;
            left:10px !important;
            right:10px !important;
            width:auto !important;
            max-width:none !important;
            max-height:calc(100vh - 90px);
            overflow-y:auto;
            z-index:10050 !important;
        }

        .inventory-range-picker .calendars-container {
            flex-direction:column;
        }
    }

    /* Date headers like the supplied inventory example. */
    #Table4 tr:first-child td.inventory-date-head {
        position:relative;
        min-width:65px;
        width:65px;
        height:52px;
        padding:4px 2px !important;
        vertical-align:middle;
        background:#fff !important;
        color:#222;
    }

    #Table4 tr:first-child td.inventory-date-head .inventory-date-day {
        display:block;
        font-size:9px;
        font-weight:700;
        line-height:11px;
        text-transform:uppercase;
        color:#4f5963;
    }

    #Table4 tr:first-child td.inventory-date-head .inventory-date-number {
        display:block;
        font-size:12px;
        font-weight:700;
        line-height:15px;
    }

    #Table4 tr:first-child td.inventory-date-head .inventory-date-month {
        display:block;
        font-size:10px;
        font-weight:500;
        line-height:13px;
        color:#4f5963;
    }

    #Table4 tr:first-child td.inventory-date-head.weekend-column {
        background:#a8e27c !important;
        color:#1f3d1d !important;
    }

    #Table4 tr:first-child td.inventory-date-head.today-column {
        background:#fff1b8 !important;
    }

    #Table4 tr:first-child td.inventory-date-head.today-column::after {
        content:"";
        position:absolute;
        top:0;
        right:0;
        width:0;
        height:0;
        border-top:10px solid #f5a623;
        border-left:10px solid transparent;
    }

    @media (max-width: 1250px) and (min-width: 768px) {
        .inventory-controls-row {
            gap:7px;
            flex-wrap:nowrap;
        }
        .inventory-toolbar-nav-wrap {
            margin-left:auto;
            width:auto;
            min-width:0;
            flex:0 1 auto;
        }
        .inventory-nav {
            width:auto;
            gap:5px;
            flex-wrap:nowrap;
            justify-content:flex-end;
        }
        .inventory-nav-btn {
            width:32px;
            min-width:32px;
            height:32px;
            font-size:19px;
        }
        .inventory-range-picker {
            width:205px;
            min-width:175px;
            height:34px;
            flex-basis:205px;
        }
        .inventory-range-input {
            height:34px;
            line-height:34px;
            padding-right:38px;
            font-size:10px;
        }
        .inventory-calendar-btn,
        .inventory-range-search {
            width:34px;
            min-width:34px;
            height:34px;
        }
    }

    @media (max-width: 767px) {
        .inventory-topbar {
            flex-wrap:wrap;
        }
        .inventory-date-controls {
            width:100%;
            margin-left:0;
            justify-content:flex-start;
            flex-wrap:wrap;
        }
        .inventory-date-controls .inventory-date-input {
            width:145px;
        }
        .inventory-controls-row {
            flex-wrap:wrap;
        }
        .inventory-toolbar-nav-wrap {
            width:100%;
            margin-left:0;
            justify-content:flex-start;
        }
        .inventory-nav {
            width:100%;
            justify-content:flex-start;
        }
        .inventory-current-date {
            min-width:135px;
        }
        .inventory-range-picker {
            width:220px;
            min-width:180px;
            flex:1 1 220px;
        }
        .inventory-range-search {
            flex:0 0 38px;
        }
    }
</style>


<style>
    /* =========================================================
       CHANNEX-STYLE INVENTORY STRUCTURE
       One scroll container + one sticky date header.
       No rate/availability/server behaviour is changed.
       ========================================================= */

    .inventory-grid-scroll {
        position: relative;
        width: 100%;
        max-height: calc(100vh - 160px);
        min-height: 320px;
        overflow: auto;
        background: #fff;
    /*    border: 1px solid #d8e0e8;*/
        border-radius: 2px;
        scrollbar-gutter: stable;
        overscroll-behavior: contain;
    }

    /* Every category uses the SAME column width and no own scrollbar. */
    .inventory-grid-scroll .inventory-category-wrap {
        width: max-content !important;
        min-width: 100% !important;
        max-width: none !important;
        padding: 0 !important;
        margin: 0 !important;
        overflow: visible !important;
    }

    .inventory-grid-scroll .inventory-category-wrap + .inventory-category-wrap {
        border-top: 1px solid #5f6770;
    }

    .inventory-grid-scroll .inventory-category-table,
    .inventory-sticky-date-header .inventory-sticky-date-table {
        width: max-content !important;
        min-width: 100% !important;
        table-layout: fixed;
        border-collapse: collapse;
        margin: 0;
    }

    /* Fixed Channex-like left column. */
    .inventory-grid-scroll .inventory-category-table td:first-child,
    .inventory-sticky-date-header .inventory-sticky-date-table td:first-child {
        width: 285px !important;
        min-width: 285px !important;
        max-width: 285px !important;
        box-sizing: border-box;
    }

    /* Every date column is identical. */
    .inventory-grid-scroll .inventory-category-table td:not(:first-child),
    .inventory-sticky-date-header .inventory-sticky-date-table td:not(:first-child) {
        width: 65px !important;
        min-width: 65px !important;
        max-width: 65px !important;
        box-sizing: border-box;
    }

    /* Keep first/room-plan column visible while moving left/right. */
    .inventory-grid-scroll .inventory-category-table td:first-child {
        position: sticky !important;
        left: 0;
        z-index: 25 !important;
        background: #fff;
        border-right: 1px solid #cfd8e3;
    }

    /* The old date row remains in the DOM for all existing logic,
       but only the single cloned header is visible. */
    .inventory-grid-scroll tr.inventory-source-date-row {
        display: none !important;
    }

    /* Single sticky date header. */
    .inventory-sticky-date-header {
        position: sticky;
        top: 0;
        z-index: 80;
        width: max-content;
        min-width: 100%;
        background: #fff;
        border-bottom: 1px solid #7c8793;
        box-shadow: 0 2px 4px rgba(15, 23, 42, .08);
    }

    .inventory-sticky-date-header .inventory-sticky-date-table td {
        height: 55px;
        padding: 4px 2px !important;
        text-align: center;
        vertical-align: middle;
        background: #fff !important;
        border: none !important;
        color: #25313c;
    }

    .inventory-sticky-date-header .inventory-sticky-date-table td:first-child {
        position: sticky;
        left: 0;
        z-index: 90;
        text-align: left;
        padding: 0 12px !important;
        background: #f7f9fb !important;
        font-weight: 700;
        color: #26313d;
        border-right: 1px solid #c8d1db;
    }

    .inventory-sticky-date-header .inventory-date-day {
        display: block;
        font-size: 9px;
        line-height: 11px;
        font-weight: 600;
        text-transform: none;
        color: #5b6570;
    }

    .inventory-sticky-date-header .inventory-date-number {
        display: block;
        font-size: 13px;
        line-height: 16px;
        font-weight: 700;
        color: #202a35;
    }

    .inventory-sticky-date-header .inventory-date-month {
        display: block;
        font-size: 9px;
        line-height: 11px;
        font-weight: 500;
        color: #6b7480;
    }

    /* Fri/Sat highlight must also be applied to the single cloned sticky header.
       The source date row is hidden, so this visible sticky header needs its own
       weekend colour instead of the previous near-white override. */
    .inventory-sticky-date-header td.weekend-column {
        background: #a8e27c !important;
        color: #1f3d1d !important;
        font-weight: 700 !important;
    }

    .inventory-sticky-date-header td.weekend-column .inventory-date-day,
    .inventory-sticky-date-header td.weekend-column .inventory-date-number,
    .inventory-sticky-date-header td.weekend-column .inventory-date-month {
        color: #1f3d1d !important;
    }

    .inventory-sticky-date-header td.today-column {
        background: #fff4d7 !important;
    }

    /*.inventory-sticky-date-header td.today-column::after {
        content: "";
        position: absolute;
        top: 0;
        right: 0;
        border-top: 9px solid #f5a623;
        border-left: 9px solid transparent;
    }*/

    .inventory-sticky-left-caption {
        display: flex;
        align-items: center;
        height: 100%;
        white-space: nowrap;
        font-size: 11px;
        letter-spacing: .1px;
    }

    /* Convert the existing Availability row into the category summary row,
       like Channex: Category on the left, AVL on the right, values by date. */
    .inventory-category-summary {
        width: 100%;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        min-height: 34px;
        padding: 0 7px;
        box-sizing: border-box;
        white-space: nowrap;
    }

    .inventory-category-summary-name {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        font-size: 11px;
        font-weight: 700;
        color: #111827;
    }

    .inventory-category-summary-avl {
        flex: 0 0 auto;
        font-size: 10px;
        font-weight: 800;
        color: #111827;
    }

    .inventory-original-availability-label {
        display: none !important;
    }

    /* Cleaner Channex-like row density. */
    .inventory-grid-scroll .inventory-category-table td {
        height: 45px;
        padding: 0 5px;

        /* IMPORTANT: force a complete visible grid around every cell.
           Using only border-color/right/bottom was too faint on the
           green/white rate backgrounds. */
        border: 1px solid #9fcfe0 !important;
        background-clip: padding-box;

        /* Extra inset separators keep the grid visible even when an inner
           textbox/background fills the complete table cell. */
        box-shadow:
            inset -1px 0 0 rgba(111, 178, 202, .35),
            inset 0 -1px 0 rgba(111, 178, 202, .35);
    }

    .inventory-grid-scroll .inventory-category-table tr:first-child td,
    .inventory-grid-scroll .inventory-category-table .inventory-date-head {
        border: 1px solid #9fcfe0 !important;
        box-shadow:
            inset -1px 0 0 rgba(111, 178, 202, .35),
            inset 0 -1px 0 rgba(111, 178, 202, .35);
    }

    /* Rate/availability/net-booking date cells: keep the same visible
       vertical separators through the complete inventory body. */
    .inventory-grid-scroll .inventory-category-table td.calCell,
    .inventory-grid-scroll .inventory-category-table tr.plan-rate-row td,
    .inventory-grid-scroll .inventory-category-table tr.standard-grid-row td {
        border:  none !important;
    }

    .inventory-grid-scroll .inventory-category-table tr.plan-rate-row td:first-child,
    .inventory-grid-scroll .inventory-category-table tr.standard-grid-row td:first-child {
        padding-left: 8px;
    }

    .inventory-grid-scroll .inventory-category-table .calInput {
        width: 65px !important;
        min-width: 65px !important;
        height: 36px !important;
        padding: 0 !important;
        box-sizing: border-box;
    }

    /* Availability row width fix.
       The date column itself is 65px wide, so the availability textbox must
       stay inside that 65px cell instead of adding the cell padding on top. */
    .inventory-grid-scroll .inventory-category-table
    td.calCell[data-rowtype="availability"] {
        width: 65px !important;
        min-width: 65px !important;
        max-width: 65px !important;
        padding: 0 !important;
        overflow: hidden;
        box-sizing: border-box;
    }

    .inventory-grid-scroll .inventory-category-table
    td.calCell[data-rowtype="availability"] .calInput {
        display: block;
        width: 100% !important;
        min-width: 0 !important;
        max-width: 65px !important;
        height: 36px !important;
        margin: 0 !important;
        padding: 0 2px !important;
        border: 0 !important;
        text-align: center !important;
        box-sizing: border-box !important;
    }

    /* Desktop scrollbar: one horizontal scrollbar at the bottom of this grid. */
    .inventory-grid-scroll::-webkit-scrollbar {
        width: 12px;
        height: 12px;
    }

    .inventory-grid-scroll::-webkit-scrollbar-thumb {
        background: #b9c1ca;
        border: 3px solid #f5f7f9;
        border-radius: 10px;
    }

    .inventory-grid-scroll::-webkit-scrollbar-track {
        background: #f5f7f9;
    }

    @media (max-width: 767px) {
        .inventory-grid-scroll {
            max-height: calc(100vh - 300px);
            min-height: 280px;
        }

        .inventory-grid-scroll .inventory-category-table td:first-child,
        .inventory-sticky-date-header .inventory-sticky-date-table td:first-child {
            width: 220px !important;
            min-width: 220px !important;
            max-width: 220px !important;
        }
    }
</style>

<div class="inventory-topbar">
    <div class="inventory-title-wrap">
        <div class="inventory-title">
            <img src="img/icons8-calender-64.png" style="height:20px; width:20px;" />
            <label class="page-header" style="margin:0;">
               INVENTORY
                <span class="tooltip">
                    <P>This section provides an overview of rate plans and rooms availability.</P>
                </span>
            </label>
        </div>

        <div class="inventory-legend" style="display:none;"> 
            <div class="containerbox">
                <div class="box uploaded"></div>
                <div class="text">Bulk Change</div>
            </div>
            <div class="containerbox">
                <div class="box save-unuploaded"></div>
                <div class="text">Yeild Applied</div>
            </div>
        </div>
    </div>

    <!-- Keep the existing server date controls because all current inventory,
         bulk update, yield and navigation logic already depends on these IDs.
         The visible range selector is rendered in the navigation row below. -->
    <div class="inventory-hidden-date-controls" aria-hidden="true">
        <asp:TextBox ID="txt_chkdate" runat="server" TextMode="Date"></asp:TextBox>
        <asp:TextBox ID="txt_chkdate2" runat="server" TextMode="Date" AutoPostBack="false"></asp:TextBox>
    </div>
</div>

<script type="text/javascript">
    function showLoader() {
        var el = document.getElementById('<%= loading.ClientID %>');
        if (el) {
            el.style.display = 'flex';
        }
    }

    function hideLoader() {
        var el = document.getElementById('<%= loading.ClientID %>');
        if (el) {
            el.style.display = 'none';
        }
    }

    function inventoryIsoToDisplay(value) {
        value = (value || '').trim();
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
        return m ? (m[3] + '/' + m[2] + '/' + m[1]) : value;
    }

    function inventoryDisplayToIso(value) {
        value = (value || '').trim();
        var m = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value);
        if (!m) return '';
        return m[3] + '-' + m[2] + '-' + m[1];
    }

    function initializeInventoryRangePicker() {
        var serverStart = document.getElementById('<%= txt_chkdate.ClientID %>');
        var serverEnd = document.getElementById('<%= txt_chkdate2.ClientID %>');
        var uiStart = document.getElementById('inventoryRangeStart');
        var uiEnd = document.getElementById('inventoryRangeEnd');
        var range = document.getElementById('inventoryDateRange');

        if (!serverStart || !serverEnd || !uiStart || !uiEnd || !range) return;

        var start = inventoryIsoToDisplay(serverStart.value);
        var end = inventoryIsoToDisplay(serverEnd.value);

        uiStart.value = start;
        uiEnd.value = end;
        range.value = (start && end) ? (start + ' - ' + end) : (start || end || '');
    }

    function prepareInventoryDateRangeSearch() {
        var serverStart = document.getElementById('<%= txt_chkdate.ClientID %>');
        var serverEnd = document.getElementById('<%= txt_chkdate2.ClientID %>');
        var uiStart = document.getElementById('inventoryRangeStart');
        var uiEnd = document.getElementById('inventoryRangeEnd');

        if (!serverStart || !serverEnd || !uiStart || !uiEnd) return false;

        var startIso = inventoryDisplayToIso(uiStart.value);
        var endIso = inventoryDisplayToIso(uiEnd.value);
        if (!startIso || !endIso || startIso > endIso) return false;

        // Convert the Payment Report calendar's dd/MM/yyyy values back to the
        // yyyy-MM-dd format already used by all AvailabilitySetup server code.
        serverStart.value = startIso;
        serverEnd.value = endIso;
        showLoader();
        return true;
    }

    window.initializeInventoryRangePicker = initializeInventoryRangePicker;
    window.prepareInventoryDateRangeSearch = prepareInventoryDateRangeSearch;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initializeInventoryRangePicker);
    } else {
        initializeInventoryRangePicker();
    }

    if (typeof Sys !== 'undefined' && Sys.WebForms && Sys.WebForms.PageRequestManager && !window.__inventoryRangePickerHooked) {
        window.__inventoryRangePickerHooked = true;
        Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
            initializeInventoryRangePicker();
        });
    }
</script>

<div class="row card rowstyle inventory-toolbar-card" style="padding:10px; margin-bottom:5px;"> 
   <div class="row inventory-controls-row" style="margin-top:0; width:100%; background-color:white; margin-left:0; margin-right:0; padding:0;">
          
        <div class="col-md-2 col-12 inventory-category-control">
            <label class="inventory-filter-label" for="<%= ddlrooms.ClientID %>">Category</label>
            <div class="inventory-category-select-wrap">
                <asp:DropDownList ID="ddlrooms" runat="server" CssClass="form-control inventory-category-select" Font-Size="11px" OnSelectedIndexChanged="btnSearchDates_Click" AutoPostBack="true"></asp:DropDownList>
            </div>
        </div> 
        <div class="col-md-3 col-12 d-flex inventory-primary-actions" style="align-items:center; gap:8px; margin-top:5px;">
            <asp:Button ID="btnAutoAvailability" runat="server"
                CssClass="btn btn-auto-availability"
                Text="⚙ Auto Update"
                OnClick="btnAutoAvailability_Click"
                OnClientClick="showLoader();"
                UseSubmitBehavior="false" />

            <!-- Point 2: one explicit Save button for all individually edited prices. -->
            <button type="button"
                id="btnSaveEditedRates"
                class="btn btn-save-edited-rates"
                onclick="saveAllEditedRates();"
                title="Save all edited daily prices">
                💾 Save
            </button>
        </div>

        <!-- Legacy duration buttons are kept hidden so existing event wiring/designer
             references remain safe. The visible navigator above controls the 30-day window. -->
        <div style="display:none;">
            <asp:Button ID="btn1" runat="server" CssClass="toggle-btn left-btn" Text="7 Days" OnClick="get7days" />
            <asp:Button ID="btn2" runat="server" CssClass="toggle-btn" Text="30 Days" OnClick="get15days"/>
            <asp:Button ID="btn3" runat="server" CssClass="toggle-btn right-btn" Text="1 Month" OnClick="get1month" /> 
        </div>
        <div class="col-md-1 d-none col-12" runat="server" id="savebtndiv">
            <asp:Button ID="Button1" OnClientClick="clicksavechanges(); return false;" Text="Save Record" Visible="false" Font-Size="11px" runat="server" CssClass="btnform-control" />
            <asp:Button ID="hiddenbutton" OnClick="Savechanges" Text="Save Record" Font-Size="11px" runat="server" CssClass="btnform-control" style="display:none;" />
        </div>
       
        <div class="col-md-2 col-12" runat="server" id="uploadbtndiv" >
             <asp:Button ID="btnShow" OnClick="openuploadpopup" Text="Upload To Channel Manager" Font-Size="11px" runat="server" CssClass="btnform-control"  />
        </div> 
        <div class="col-md-1 col-6" runat="server" id="bulkratebtndiv" style="display:none;">
            <asp:Button ID="Button3" OnClientClick="clickbulksavechanges(); return false;" Text="Bulk Rate" Font-Size="11px" runat="server" CssClass="btnform-control" />
            <asp:Button ID="hiddenbutton2" OnClick="openbulkuploadpopup" Text="Save Bulk Record" CommandArgument="rate" Font-Size="11px" runat="server" CssClass="btnform-control" style="display:none;" />
        </div> 
       <div class="col-md-1 col-6" style="display:none;">
            <asp:Button ID="Button10" OnClientClick="clickrestrictions(); return false;" Text="Restriction" Font-Size="11px" runat="server" CssClass="btnform-control" />
            <asp:Button ID="Button11" OnClick="openrestrictionspopup" Text="Restriction" Font-Size="11px" runat="server" CssClass="btnform-control" style="display:none;" />
        </div> 
        <div class="col-md-2 col-6" runat="server" id="bulkavailabilitybtndiv">
            <asp:Button ID="btnBulkUPloadAvailibilty" OnClientClick="clickbulksavechanges2(); return false;" Text="Bulk Availablility" Font-Size="11px" runat="server" CssClass="btnform-control" />
            <asp:Button ID="Button7" OnClick="openbulkuploadavailabilitypopup" Text="Save Bulk Record" CommandArgument="available" Font-Size="11px" runat="server" CssClass="btnform-control" style="display:none;" />
        </div> 
        <div class="col-md-2 col-6" runat="server" id="divapplyyeild">
    
     <asp:Button ID="btnApplyyeild" OnClick="btn_callYeildclick" Text="Apply Yeild" CommandArgument="available" Font-Size="11px" runat="server" CssClass="btnform-control"  />
 </div> 

        <div class="col-md-auto col-12 inventory-toolbar-nav-wrap">
    <div class="inventory-nav">
        <asp:LinkButton ID="btnPrevInventoryWindow" runat="server"
            CssClass="inventory-nav-btn"
            ToolTip="Previous 30 days"
            Text="«"
            CausesValidation="false"
            OnClientClick="showLoader();"
            OnClick="btnPrevInventoryWindow_Click" />

        <asp:LinkButton ID="btnPrevInventoryDay" runat="server" Visible="false"
            CssClass="inventory-nav-btn"
            ToolTip="Previous day"
            Text="‹"
            CausesValidation="false"
            OnClientClick="showLoader();"
            OnClick="btnPrevInventoryDay_Click" />

        <!-- Date-range selector: same two-month calendar used on Payment Report. -->
        <div class="inventory-range-picker">
            <input type="text"
                id="inventoryDateRange"
                class="inventory-range-input"
                readonly="readonly"
                aria-label="Inventory date range"
                placeholder="dd/mm/yyyy - dd/mm/yyyy" />

            <input type="hidden" id="inventoryRangeStart" />
            <input type="hidden" id="inventoryRangeEnd" />

            <button type="button"
                class="inventory-calendar-btn calendar-icon"
                title="Select date range"
                aria-label="Select date range"
                onclick="showCalendarFor('inventoryDateRange', 'inventoryRangeStart', 'inventoryRangeEnd', 'range')">
                <i class="far fa-calendar-alt" aria-hidden="true"></i>
            </button>

            <div class="calendar-popup" id="calendarPopup">
                <div class="calendar-header" style="display:flex; justify-content:space-between;">
                    <div class="calendar-month-header">
                        <button class="nav-btn" type="button" onclick="previousLeftMonth()">&lt;</button>
                        <span id="leftMonthHeader"></span>
                        <button class="nav-btn" type="button" onclick="nextLeftMonth()">&gt;</button>
                    </div>

                    <div class="calendar-month-header">
                        <button class="nav-btn" type="button" onclick="previousRightMonth()">&lt;</button>
                        <span id="rightMonthHeader"></span>
                        <button class="nav-btn" type="button" onclick="nextRightMonth()">&gt;</button>
                    </div>
                </div>

                <div class="calendars-container">
                    <div class="calendar-month">
                        <div class="calendar-grid" id="leftCalendar"></div>
                    </div>
                    <div class="calendar-month">
                        <div class="calendar-grid" id="rightCalendar"></div>
                    </div>
                </div>

                <div class="calendar-footer">
                    <div class="date-range-text" id="selectedRangeText">Select date range</div>
                    <div class="calendar-actions">
                        <button class="btn btn-clear" type="button" onclick="clearSelection()">Clear</button>
                        <button class="btn btn-apply" type="button" onclick="applySelection()">Apply</button>
                    </div>
                </div>
            </div>
        </div>

        <asp:LinkButton ID="btnSearchDates" runat="server"
            CssClass="inventory-range-search"
            ToolTip="View availability between selected dates"
            OnClientClick="return prepareInventoryDateRangeSearch();"
            OnClick="btnSearchDates_Click"
            CausesValidation="false">
            <i class="bi bi-search" aria-hidden="true"></i>
        </asp:LinkButton>

        <!-- Kept hidden so the existing server event/designer wiring is not disturbed. -->
        <asp:LinkButton ID="btnInventoryToday" runat="server"
            Style="display:none;"
            CausesValidation="false"
            OnClick="btnInventoryToday_Click" />

        <asp:LinkButton ID="btnNextInventoryDay" runat="server"
            CssClass="inventory-nav-btn"
            ToolTip="Next day"
            Text="›"
            CausesValidation="false"
            OnClientClick="showLoader();"
            OnClick="btnNextInventoryDay_Click" Visible="false" />

        <asp:LinkButton ID="btnNextInventoryWindow" runat="server"
            CssClass="inventory-nav-btn"
            ToolTip="Next 30 days"
            Text="»"
            CausesValidation="false"
            OnClientClick="showLoader();"
            OnClick="btnNextInventoryWindow_Click" />
    </div>
        </div>

      

      </div>
    <asp:HiddenField runat="server" ID="hf_property_id" />
    <style>
          #Table4 td.room-status 
          {
              width:400px;
              cursor: pointer;
          }
          .tooltip
          {
              box-shadow: 0 4px 8px rgba(0, 0, 0, 0.2);
              font-family: Arial, sans-serif;
              max-width: 200px;
          }
      </style>
  <style>
  /* light red wash for the whole date column */
  .zero-col {
    
  }
  /* ensure inner controls also get tinted */
  td.zero-col * {
   
  }
  /* the red rounded badge for the Availability=0 input */
  .zero-badge {
    height: 33px !important;
    width: 30px !important;
    text-align: center !important;
    border: none !important;
    background: rgb(207, 45, 45) !important;
    color: #fff !important;
    border-radius: 5px !important;
    margin: 5px !important;
    font-weight: bold !important;
    line-height: 30px !important; /* centers the “0” vertically */
    padding: 0 !important;         /* keep the width exact */
  }


  /* ================================================================
     FINAL CELL BORDER FIX
     ----------------------------------------------------------------
     The 65px TextBox was wider than the usable TD content area once
     padding/borders were included. The input therefore painted over
     some vertical cell borders. Keep every editor INSIDE its TD and
     draw the separator above the cell content.
     ================================================================ */
  .inventory-grid-scroll .inventory-category-table td.calCell {
      padding: 0 !important;
      overflow: hidden !important;
      position: relative;
      border: none !important;
      box-shadow: none !important;
      background-clip: padding-box !important;
  }

  /* Highlight only while hovering a body cell.
     Normal cells remain borderless and their existing background colour is preserved. */
  .inventory-grid-scroll .inventory-category-table td.calCell:hover {
      z-index: 2;
      box-shadow: inset 0 0 0 2px rgba(69, 123, 157, .55) !important;
  }

  .inventory-grid-scroll .inventory-category-table
  td.calCell .calInput:not(.zero-badge) {
      display: block !important;
      width: 100% !important;
      min-width: 0 !important;
      max-width: 100% !important;
      height: 36px !important;
      margin: 0 !important;
      padding: 0 2px !important;
      border: 0 !important;
      box-sizing: border-box !important;
  }

  /* Draw the grid separator over the textbox/background so it cannot
     disappear on white, green, yellow or stop-sell cells. */
  .inventory-grid-scroll .inventory-category-table td.calCell::after {
      display: none !important;
  }

  /* Non-editor cells (for example Net Booking output cells) also keep
     the same continuous grid line. */
  .inventory-grid-scroll .inventory-category-table
  tr.standard-grid-row td:not(:first-child),
  .inventory-grid-scroll .inventory-category-table
  tr.plan-rate-row td:not(:first-child) {
      border: none !important;
      box-shadow: none !important;
  }

  /* Keep Availability=0 badge at the original compact width.
     This selector is intentionally more specific than the Channex grid
     availability textbox rule above, so width:100% cannot stretch it. */
  .inventory-grid-scroll .inventory-category-table
  td.calCell[data-rowtype="availability"] .calInput.zero-badge {
      display: block !important;
      width: 30px !important;
      min-width: 30px !important;
      max-width: 30px !important;
      height: 33px !important;
      margin: 1px auto !important;
      padding: 0 !important;
      border-radius: 5px !important;
      line-height: 30px !important;
      box-sizing: border-box !important;
  }
</style>

<style>
    /* ================================================================
       FINAL INVENTORY SPACING + COMPACT AVL ROW
       ================================================================ */

    /* Add breathing space between the filters/actions toolbar and the
       sticky inventory date/table header. */
    .inventory-grid-scroll {
        margin-top: 12px !important;
    }

    /* Reduce ONLY the category Availability/AVL row height. */
    .inventory-grid-scroll .inventory-category-table
    tr.inventory-availability-row > td {
        height: 30px !important;
        min-height: 30px !important;
        padding-top: 0 !important;
        padding-bottom: 0 !important;
    }

    .inventory-grid-scroll .inventory-category-table
    tr.inventory-availability-row .inventory-category-summary {
        min-height: 28px !important;
        height: 28px !important;
        padding-top: 0 !important;
        padding-bottom: 0 !important;
    }

    .inventory-grid-scroll .inventory-category-table
    tr.inventory-availability-row td.calCell .calInput:not(.zero-badge) {
        height: 30px !important;
        line-height: 30px !important;
    }

    /* Keep the red zero badge compact and vertically centered in the
       shorter Availability row. */
    .inventory-grid-scroll .inventory-category-table
    tr.inventory-availability-row td.calCell .calInput.zero-badge {
        width: 30px !important;
        min-width: 30px !important;
        max-width: 30px !important;
        height: 26px !important;
        line-height: 26px !important;
        margin: 2px auto !important;
    }
</style>

<style>
    /* ================================================================
       PROFESSIONAL INVENTORY HEADER / FILTER BAR
       UI-only. Existing IDs, postbacks and inventory logic are unchanged.
       ================================================================ */

    /* The navigation already identifies this page as Inventory, so the
       separate blue INVENTORY banner is intentionally hidden. Hidden date
       controls inside it remain in the DOM and continue to work normally. */
    .inventory-topbar {
        display: none !important;
    }

    .inventory-toolbar-card {
        margin: 10px 0 0 !important;
        padding: 9px 12px !important;
        background: #ffffff !important;
        border: 1px solid #dbe4ed !important;
        border-radius: 8px !important;
        box-shadow: 0 2px 8px rgba(15, 23, 42, .055) !important;
        overflow: visible !important;
    }

    .inventory-toolbar-card .inventory-controls-row {
        min-height: 44px;
        display: flex !important;
        align-items: center !important;
        flex-wrap: nowrap !important;
        gap: 10px !important;
        margin: 0 !important;
        padding: 0 !important;
        background: transparent !important;
    }

    /* Category control */
    .inventory-toolbar-card .inventory-category-control {
        flex: 0 0 300px !important;
        width: 300px !important;
        max-width: 300px !important;
        min-width: 255px !important;
        padding: 0 !important;
        display: flex !important;
        align-items: center !important;
        gap: 9px !important;
    }

    .inventory-toolbar-card .inventory-filter-label {
        flex: 0 0 auto;
        margin: 0 !important;
        color: #475569;
        font-size: 10px;
        font-weight: 700;
        letter-spacing: .35px;
        text-transform: uppercase;
        white-space: nowrap;
    }

    .inventory-category-select-wrap {
        position: relative;
        flex: 1 1 auto;
        min-width: 0;
    }

    .inventory-category-select-wrap::after {
        content: "";
        position: absolute;
        right: 12px;
        top: 50%;
        width: 7px;
        height: 7px;
        border-right: 1.5px solid #64748b;
        border-bottom: 1.5px solid #64748b;
        transform: translateY(-65%) rotate(45deg);
        pointer-events: none;
    }

    .inventory-toolbar-card select.inventory-category-select {
        width: 100% !important;
        height: 36px !important;
        min-height: 36px !important;
        margin: 0 !important;
        padding: 0 35px 0 12px !important;
        border: 1px solid #cbd5e1 !important;
        border-radius: 6px !important;
        outline: none !important;
        background: #fff !important;
        color: #334155 !important;
        font-size: 11px !important;
        font-weight: 500 !important;
        line-height: 36px !important;
        box-shadow: 0 1px 2px rgba(15, 23, 42, .03) !important;
        appearance: none !important;
        -webkit-appearance: none !important;
        -moz-appearance: none !important;
        cursor: pointer;
        transition: border-color .16s ease, box-shadow .16s ease, background .16s ease;
    }

    .inventory-toolbar-card select.inventory-category-select:hover {
        border-color: #94a3b8 !important;
        background: #fcfdff !important;
    }

    .inventory-toolbar-card select.inventory-category-select:focus {
        border-color: #f5a623 !important;
        box-shadow: 0 0 0 3px rgba(245, 166, 35, .13) !important;
        background: #fff !important;
    }

    /* Main actions */
    .inventory-toolbar-card .inventory-primary-actions {
        flex: 0 0 auto !important;
        width: auto !important;
        max-width: none !important;
        margin: 0 !important;
        padding: 0 !important;
        align-items: center !important;
    }

    .inventory-toolbar-card .btn-auto-availability,
    .inventory-toolbar-card .btn-save-edited-rates {
        height: 36px !important;
        min-height: 36px !important;
        padding: 0 15px !important;
        border-radius: 6px !important;
        display: inline-flex !important;
        align-items: center !important;
        justify-content: center !important;
        font-size: 10.5px !important;
        font-weight: normal !important;
        letter-spacing: .25px !important;
        line-height: 1 !important;
        box-shadow: none !important;
    }

    .inventory-toolbar-card .btn-auto-availability {
        background: #457b9d !important;




        border: 1px solid #457b9d !important;
    }

    .inventory-toolbar-card .btn-auto-availability:hover,
    .inventory-toolbar-card .btn-auto-availability:focus {
        background: #138d75 !important;
        transform: none !important;
        box-shadow: 0 0 0 3px rgba(22, 160, 133, .12) !important;
    }

    .inventory-toolbar-card .btn-save-edited-rates {
        background: #ffffff !important;
        color: #475569 !important;
        border: 1px solid #cbd5e1 !important;
    }

    .inventory-toolbar-card .btn-save-edited-rates:hover,
    .inventory-toolbar-card .btn-save-edited-rates:focus {
        background: #f8fafc !important;
        color: #1f2937 !important;
        border-color: #94a3b8 !important;
        box-shadow: 0 0 0 3px rgba(148, 163, 184, .10) !important;
    }

    /* When at least one rate is changed, restore the earlier red Save action. */
    .inventory-toolbar-card .btn-save-edited-rates.has-rate-changes,
    .inventory-toolbar-card .btn-save-edited-rates.has-rate-changes:hover,
    .inventory-toolbar-card .btn-save-edited-rates.has-rate-changes:focus {
        background: #dc2626 !important;
        color: #ffffff !important;
        border-color: #dc2626 !important;
        box-shadow: none !important;
    }

    /* Right-side date navigation */
    .inventory-toolbar-card .inventory-toolbar-nav-wrap {
        margin-left: auto !important;
        padding: 0 !important;
        flex: 0 0 auto !important;
        width: auto !important;
        max-width: none !important;
    }

    .inventory-toolbar-card .inventory-nav {
        gap: 6px !important;
        flex-wrap: nowrap !important;
    }

    .inventory-toolbar-card .inventory-nav-btn {
        width: 34px !important;
        min-width: 34px !important;
        height: 34px !important;
        border-radius: 50% !important;
        background: #f5a623 !important;
        box-shadow: none !important;
        font-size: 19px !important;
    }

    .inventory-toolbar-card .inventory-nav-btn:hover,
    .inventory-toolbar-card .inventory-nav-btn:focus {
        background: #e99a16 !important;
        transform: none !important;
        box-shadow: 0 0 0 3px rgba(245, 166, 35, .12) !important;
    }

    .inventory-toolbar-card .inventory-range-picker {
        width: 230px !important;
        min-width: 230px !important;
        height: 36px !important;
        flex: 0 0 230px !important;
    }

    .inventory-toolbar-card .inventory-range-input {
        height: 36px !important;
        line-height: 36px !important;
        border: 1px solid #cbd5e1 !important;
        border-radius: 6px !important;
        padding: 0 42px 0 11px !important;
        color: #334155 !important;
        background: #fff !important;
        font-size: 10.5px !important;
        box-shadow: none !important;
    }

    .inventory-toolbar-card .inventory-range-input:focus {
        border-color: #f5a623 !important;
        box-shadow: 0 0 0 3px rgba(245, 166, 35, .12) !important;
    }

    .inventory-toolbar-card .inventory-calendar-btn {
        width: 36px !important;
        height: 36px !important;
        border-radius: 0 6px 6px 0 !important;
        background: #f5a623 !important;
        border-left: 1px solid #e99a16 !important;
    }

    .inventory-toolbar-card .inventory-range-search {
        width: 36px !important;
        min-width: 36px !important;
        height: 36px !important;
        border-radius: 6px !important;
        background: #457b9d !important;
        box-shadow: none !important;
    }

    .inventory-toolbar-card .inventory-range-search:hover,
    .inventory-toolbar-card .inventory-range-search:focus {
        background: #386b8a !important;
        box-shadow: 0 0 0 3px rgba(69, 123, 157, .12) !important;
    }

    /* Make the visible sticky date header cleaner without changing Fri/Sat. */
    .inventory-sticky-date-header {
    /*    border: 1px solid #d7e0e8 !important;
        border-bottom: 1px solid #aebbc8 !important;
        box-shadow: 0 2px 5px rgba(15, 23, 42, .07) !important;*/
    }

    .inventory-sticky-date-header .inventory-sticky-date-table td {
/*        background: #f8fafc !important;
        border-color: #dce4ec !important;*/
    }

    .inventory-sticky-date-header .inventory-sticky-date-table td:first-child {
        background: #f3f6f9 !important;
        color: #1f2937 !important;
        font-size: 11px !important;
        letter-spacing: .1px;
    }

    /* Preserve the requested Friday/Saturday green highlight. */
    .inventory-sticky-date-header td.weekend-column {
        background: #a8e27c !important;
        color: #1f3d1d !important;
    }

    .inventory-sticky-date-header td.weekend-column .inventory-date-day,
    .inventory-sticky-date-header td.weekend-column .inventory-date-number,
    .inventory-sticky-date-header td.weekend-column .inventory-date-month {
        color: #1f3d1d !important;
    }

    .inventory-sticky-date-header td.today-column:not(.weekend-column) {
        background: #fff4d7 !important;
    }

    /* Existing 12px grid gap looked too large after the toolbar became a card. */
    .inventory-grid-scroll {
        margin-top: 4px !important;
    }

    @media (max-width: 1100px) {
        .inventory-toolbar-card .inventory-controls-row {
            flex-wrap: wrap !important;
        }
        .inventory-toolbar-card .inventory-category-control {
            flex: 1 1 280px !important;
            width: auto !important;
            max-width: 360px !important;
        }
        .inventory-toolbar-card .inventory-toolbar-nav-wrap {
            margin-left: auto !important;
        }
    }

    @media (max-width: 767px) {
        .inventory-toolbar-card {
            padding: 9px !important;
        }
        .inventory-toolbar-card .inventory-category-control,
        .inventory-toolbar-card .inventory-primary-actions,
        .inventory-toolbar-card .inventory-toolbar-nav-wrap {
            width: 100% !important;
            max-width: none !important;
            flex: 1 1 100% !important;
        }
        .inventory-toolbar-card .inventory-toolbar-nav-wrap {
            margin-left: 0 !important;
        }
        .inventory-toolbar-card .inventory-nav {
            width: 100% !important;
            justify-content: flex-start !important;
            flex-wrap: wrap !important;
        }
        .inventory-toolbar-card .inventory-range-picker {
            flex: 1 1 205px !important;
            width: auto !important;
            min-width: 185px !important;
        }
    }
</style>
    <div class="col-md-12 col-12" style="padding:0px;">
        <asp:HiddenField ID="hiddenroom" runat="server" />
        <asp:HiddenField ID="hiddenDate" runat="server" />
        <asp:UpdatePanel ID="updatepopupfdo" runat="server" UpdateMode="Conditional">
            <ContentTemplate>
    <div id="maindiv" runat="server" class="inventory-grid-scroll">
    <asp:Repeater ID="mainRepeater" runat="server" OnItemDataBound="mainRepeater_ItemDataBound">
    <ItemTemplate>
        <div class="col-12 inventory-category-wrap" id='<%# "tablediv_" + Container.ItemIndex %>'>  
            <table id="Table4" class="inventory-category-table" style="font-size:11px;">
                <tr>
                    <td style="background-color:#20b996; text-align:left;">
                        <asp:Label ID="Label100" runat="server" Visible="false" style="white-space:nowrap; font-size: 15px; color: #545454;" Text='<%# Eval("description") %>' />
                        <label style="white-space:nowrap; width:200px; font-weight:bold; color:white;">
                            <%# Eval("description") %>(<%# Eval("no_of_rooms") %>)
                        </label>
                        <asp:Label ID="Label1" runat="server" Visible="false" style="font-size:15px; color:#545454;" Text='<%# Eval("catgeoryid") %>' />
                    </td>

                    <asp:Repeater ID="DatesRepeater" runat="server">
                        <ItemTemplate>
                            <td class='<%# "inventory-date-head" +
                                (Convert.ToBoolean(Eval("IsWeekend")) ? " weekend-column" : "") +
                                (Convert.ToBoolean(Eval("IsToday")) ? " today-column" : "") %>'
                                title='<%# Eval("Date") %>'>
                                <span class="inventory-date-day"><%# Eval("DayShort") %></span>
                                <span class="inventory-date-number"><%# Eval("DayNumber") %></span>
                                <span class="inventory-date-month"><%# Eval("MonthShort") %></span>
                            </td>
                        </ItemTemplate>
                    </asp:Repeater>
                </tr>

                <asp:Repeater ID="RoomNoRepeater" runat="server" OnItemDataBound="RoomNoRepeater_ItemDataBound">
                    <ItemTemplate>
                        <tr class='<%# (Eval("planname") != null && Eval("planname").ToString().EndsWith("(Rate)"))
                            ? "plan-rate-row"
                            : "standard-grid-row" %>'
                            data-plan-group='<%# Eval("category_id") + "||" + Eval("planid") %>'
                            data-isderived='<%# Convert.ToBoolean(Eval("isDerived")) ? "1" : "0" %>'
                            data-derivedlocked='<%# Convert.ToBoolean(Eval("isDerived")) && !_allowDerivedRateEditing ? "1" : "0" %>'>
                            <td style="text-align:left;">
                                <button type="button"
                                    class="restriction-toggle"
                                    title="Show or hide restrictions for this rate plan"
                                    data-group='<%# Eval("category_id") + "||" + Eval("planid") %>'
                                    style='<%# (Eval("planname") != null &&
                                        Eval("planname").ToString().EndsWith("(Rate)") &&
                                        _canViewRestriction)
                                        ? ""
                                        : "display:none;" %>'>
                                    <span class="restriction-toggle-icon">›</span>
                                </button>

                                <asp:Label
                                    runat="server"
                                    ID="plannamelbl"
                                    CssClass="plan-name-label"
                                    Text='<%# Eval("planname") %>' />
                                <asp:Label
                                    runat="server"
                                    ID="derivedRateBadge"
                                    CssClass="derived-rate-lock"
                                    Visible='<%# Convert.ToBoolean(Eval("isDerived")) && !_allowDerivedRateEditing %>'
                                    Text="🔒"
                                    ToolTip="Derived rate plan editing is disabled for this hotel." />
                                <asp:Label style="white-space:nowrap; font-weight:normal; font-size:1px; visibility:hidden"
                                    runat="server" ID="planid" Text='<%# Eval("planid") %>' />
                            </td>

                            <asp:Repeater ID="statusRepeater" runat="server">
                                <ItemTemplate>
                                    <td class='<%# "calCell" +
                                            (Convert.ToBoolean(Eval("stop_sell"))
                                                ? " effective-stop-sell"
                                                : "") +
                                            GetWeekendClass(Eval("date")) %>'
                                        data-date="<%# Eval("date","{0:yyyy-MM-dd}") %>"
                                        data-effective-stop-sell="<%# Convert.ToBoolean(Eval("stop_sell")) ? "1" : "0" %>"
                                        data-block-reason="<%# Eval("block_reason") %>"
                                        data-plan="<%# Eval("planid") %>"
                                        data-category="<%# DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "category_id") %>"
                                        data-rowtype="<%# Eval("rowtype") %>"
                                        data-isderived="<%# Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) ? "1" : "0" %>"
                                        data-derivedlocked="<%# Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) &&
                                            !_allowDerivedRateEditing ? "1" : "0" %>"
                                        data-canupdaterate="<%# _canUpdateRate &&
                                            (!Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) ||
                                             _allowDerivedRateEditing)
                                            ? "1"
                                            : "0" %>"
                                        data-canbulkrateupdate="<%# _canBulkRateUpdate &&
                                            (!Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) ||
                                             _allowDerivedRateEditing)
                                            ? "1"
                                            : "0" %>"
                                        data-canupdaterestriction="<%# _canUpdateRestriction ? "1" : "0" %>"
                                        data-planname="<%# DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "planname") %>"
                                        data-categoryname="<%# DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "category_name") %>"
                                        title='<%# !string.IsNullOrWhiteSpace(Convert.ToString(Eval("block_reason")))
                                            ? "Blocked by " + Convert.ToString(Eval("block_reason")) + "."
                                            : Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) &&
                                              !_allowDerivedRateEditing
                                                ? "Derived rate: edit the parent rate plan or its offset configuration."
                                                : "" %>'
                                        style='<%# "padding:0px; background-color:" +
                                            GetBgColor(Eval("isreadonly"), Eval("Color"), Eval("upload"),
                                                       Eval("uploadfrom"), Eval("no_of_rooms"),
                                                       Eval("rate"), false) %>'>

                                        <asp:TextBox
                                            runat="server"
                                            ID="txtrate"
                                            CssClass="calInput"
                                            Style='<%# "height:36px; text-align:center; border:none; width:65px; background-color:" +
                                                GetBgColor(Eval("isreadonly"), Eval("Color"), Eval("upload"), Eval("uploadfrom"), Eval("no_of_rooms"), Eval("rate"), false) %>'
                                            Text='<%# (Eval("no_of_rooms") != null && Eval("no_of_rooms").ToString().Trim() != "") ? Eval("no_of_rooms") : Eval("rate") %>'
                                            Enabled="true"
                                            ReadOnly='<%# Convert.ToBoolean(DataBinder.Eval(((RepeaterItem)Container.NamingContainer.NamingContainer).DataItem, "isDerived")) &&
                                                !_allowDerivedRateEditing %>'></asp:TextBox>

                                        <asp:HiddenField
                                            runat="server"
                                            ID="orig_no_of_rooms"
                                            Value='<%# (Eval("no_of_rooms") ?? "").ToString().Trim() %>' />
                                    </td>
                                </ItemTemplate>
                            </asp:Repeater>
                        </tr>

                        <!-- Hidden by default. Each plan can be expanded independently. -->
                        <asp:Repeater ID="restrictionRowsRepeater" runat="server">
                            <ItemTemplate>
                                <tr class="restriction-grid-row"
                                    data-restriction-group="<%# Eval("groupKey") %>"
                                    data-restriction-type="<%# Eval("restrictionKey") %>"
                                    style="display:none;">
                                    <td class="restriction-name-cell"
                                        title='<%# Eval("description") %>'>
                                        <span class="restriction-tree">↳</span>
                                        <span class="restriction-row-name"><%# Eval("displayName") %></span>
                                        <span class="restriction-info" aria-hidden="true">ⓘ</span>
                                    </td>

                                    <asp:Repeater ID="restrictionValueRepeater" runat="server" DataSource='<%# Eval("values") %>'>
                                        <ItemTemplate>
                                            <td class='<%# "calCell restrictionCell " +
                                                    (_canUpdateRestriction
                                                        ? "restriction-editable-cell"
                                                        : "restriction-readonly-cell") +
                                                    GetWeekendClass(Eval("date")) %>'
                                                data-date="<%# Eval("date","{0:yyyy-MM-dd}") %>"
                                                data-plan="<%# Eval("planid") %>"
                                                data-category="<%# Eval("category_id") %>"
                                                data-rowtype="<%# Eval("rowtype") %>"
                                                data-current="<%# Eval("rawValue") %>"
                                                data-cutoff-mode="<%# Eval("cutoffMode") %>"
                                                data-cutoff-days="<%# Eval("cutoffDays") %>"
                                                data-canupdaterate="0"
                                                data-canupdaterestriction="<%# _canUpdateRestriction ? "1" : "0" %>"
                                                data-planname="<%# Eval("planname") %>"
                                                data-categoryname="<%# Eval("category_name") %>">
                                                <span class='<%# "restriction-grid-value " + Eval("valueClass") +
                                                        (string.Equals(Convert.ToString(Eval("rowtype")),
                                                            "booking_cutoff",
                                                            StringComparison.OrdinalIgnoreCase)
                                                            ? " restriction-cutoff-value"
                                                            : "") %>'
                                                    title='<%# Eval("tooltipText") %>'>
                                                    <span class="restriction-standard-text"
                                                        style='<%# string.Equals(Convert.ToString(Eval("rowtype")),
                                                            "booking_cutoff",
                                                            StringComparison.OrdinalIgnoreCase)
                                                            ? "display:none;"
                                                            : "" %>'>
                                                        <%# Eval("displayValue") %>
                                                    </span>

                                                    <span class="cutoff-mode-text"
                                                        style='<%# string.Equals(Convert.ToString(Eval("rowtype")),
                                                            "booking_cutoff",
                                                            StringComparison.OrdinalIgnoreCase)
                                                            ? ""
                                                            : "display:none;" %>'>
                                                        <%# Eval("cutoffCompactMode") %>
                                                    </span>
                                                    <span class="cutoff-status-line"
                                                        style='<%# string.Equals(Convert.ToString(Eval("rowtype")),
                                                            "booking_cutoff",
                                                            StringComparison.OrdinalIgnoreCase)
                                                            ? ""
                                                            : "display:none;" %>'>
                                                        <span class='<%# "cutoff-state-dot " + Eval("cutoffStateClass") %>'></span>
                                                        <span class="cutoff-state-text"><%# Eval("cutoffStatusText") %></span>
                                                    </span>
                                                </span>
                                            </td>
                                        </ItemTemplate>
                                    </asp:Repeater>
                                </tr>
                            </ItemTemplate>
                        </asp:Repeater>
                    </ItemTemplate>
                </asp:Repeater>
            </table>
        </div>
    </ItemTemplate>
</asp:Repeater>
<!-- Optional, tiny CSS hint for changed cells -->
<style>
    .changed-cell { outline: 2px dashed #ff6b35; }
</style>
        <style>
  .calCell { user-select:none; cursor:pointer; }
  .calCell .calInput { pointer-events:none; } /* ✅ allow dragging on textbox */
  .calCell[data-rowtype="availability"],
.calCell[data-rowtype="availability"] .calInput,
.calCell[data-rowtype="net"],
.calCell[data-rowtype="net"] .calInput {
    font-weight:700 !important;
}
  .calCell.bulkSelected { outline:2px solid #20b996; box-shadow: inset 0 0 0 9999px rgba(32,185,150,.12); }

  .restriction-toggle{
      width:22px; height:22px; padding:0; margin-right:5px;
      border:1px solid #cbd5e1; background:#f8fafc; color:#334155;
      display:inline-flex; align-items:center; justify-content:center;
      cursor:pointer; vertical-align:middle; border-radius:4px;
  }
  .restriction-toggle:hover{ background:#e2e8f0; }
  .restriction-toggle-icon{
      display:block; font-size:18px; line-height:18px;
      transition:transform .18s ease;
  }
  .restriction-toggle.is-open .restriction-toggle-icon{ transform:rotate(90deg); }

  #Table4 tr.restriction-grid-row td{
      background:#f8fafc;
      border-color:#e2e8f0;
      height:31px;
      padding:0 5px;
  }
  #Table4 tr.restriction-grid-row td:first-child{
      background:#f1f5f9;
  }
  .restriction-name-cell{
      text-align:left !important;
      white-space:nowrap;
      cursor:help;
  }
  .restriction-tree{ color:#94a3b8; margin:0 6px 0 20px; }
  .restriction-row-name{ color:#475569; font-size:10px; font-weight:600; }
  .restriction-info{
      margin-left:5px;
      color:#64748b;
      font-size:10px;
      vertical-align:middle;
  }
  .restrictionCell{ text-align:center; }
  .restriction-editable-cell{ cursor:crosshair; }
  .restriction-readonly-cell{ cursor:default; }
  .restriction-readonly-cell .restriction-grid-value{ opacity:.88; }
  .restriction-grid-value{
      min-width:46px; height:22px; padding:2px 6px;
      display:inline-flex; align-items:center; justify-content:center;
      border-radius:3px; font-size:10px; font-weight:500;
      white-space:nowrap;
  }
  .restriction-empty{ color:#94a3b8; background:transparent; }
  .restriction-number{ color:#1e3a5f; background:#e0f2fe; border:1px solid #bae6fd; }
  .restriction-zero{ color:#64748b; background:#f1f5f9; border-color:#e2e8f0; }
  .restriction-open{ color:#166534; background:#dcfce7; border:1px solid #bbf7d0; }
  .restriction-closed{ color:#991b1b; background:#fee2e2; border:1px solid #fecaca; }
  .restriction-cutoff-open{
      color:#166534;
      background:#ecfdf5;
      border:1px solid #a7f3d0;
  }
  .restriction-cutoff-closed{
      color:#991b1b;
      background:#fef2f2;
      border:1px solid #fecaca;
  }
  .restriction-cutoff-disabled{
      color:#475569;
      background:#f8fafc;
      border:1px solid #dbe3ec;
  }
  #Table4 tr[data-restriction-type="booking_cutoff"] td{
      height:40px;
  }
  .restriction-cutoff-value{
      width:62px;
      min-width:62px;
      height:32px;
      padding:3px 2px;
      display:inline-flex;
      flex-direction:column;
      align-items:center;
      justify-content:center;
      gap:2px;
      overflow:hidden;
      line-height:1;
  }
  .cutoff-mode-text{
      width:100%;
      overflow:hidden;
      color:#334155;
      font-size:8.5px;
      font-weight:600;
      line-height:1.05;
      text-align:center;
      text-overflow:ellipsis;
      white-space:nowrap;
  }
  .cutoff-status-line{
      display:inline-flex;
      align-items:center;
      justify-content:center;
      gap:3px;
      line-height:1;
      white-space:nowrap;
  }
  .cutoff-state-dot{
      width:6px;
      height:6px;
      flex:0 0 6px;
      border-radius:50%;
  }
  .cutoff-state-open{ background:#16a34a; }
  .cutoff-state-closed{ background:#dc2626; }
  .cutoff-state-text{
      color:inherit;
      font-size:8.5px;
      font-weight:500;
      line-height:1;
      white-space:nowrap;
  }
</style>
        <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>


<script>
    function intOrNull(v) {
        v = (v ?? "").toString().trim();
        if (v === "") return null;
        var n = Number(v);
        return Number.isFinite(n) ? n : null;
    }

    function clearZeroDecor(table) {
        // remove column tint + badges from a previous pass
        table.querySelectorAll(".zero-col").forEach(el => el.classList.remove("zero-col"));
        table.querySelectorAll(".zero-badge").forEach(el => el.classList.remove("zero-badge"));
    }

    // Force a TD to column-tint (header/body)
    function tintTd(td) {
        if (!td) return;
        td.classList.add("zero-col");
    }

    // Apply the red rounded “badge” style to the Availability input for 0
    function badgeInput(tb) {
        if (!tb) return;
        tb.classList.add("zero-badge");
    }

    // Main: for one table wrapper div id="tablediv_X"
    function highlightByAvailabilityZero(container) {
        if (!container) return;
        var table = container.querySelector("table");
        if (!table) return;

        clearZeroDecor(table);

        var rows = table.querySelectorAll("tr");
        if (!rows.length) return;

        var headerCells = rows[0].querySelectorAll("td,th");

        // find Availability row (first cell contains “Availability”)
        var availRow = null;
        for (var r = 1; r < rows.length; r++) {
            var firstTd = rows[r].querySelector("td");
            if (!firstTd) continue;
            var lbl = firstTd.querySelector("[id$='plannamelbl']");
            var text = (lbl ? lbl.textContent : firstTd.textContent || "").trim().toLowerCase();
            if (text.startsWith("availability")) { availRow = rows[r]; break; }
        }
        if (!availRow) return;

        var availCells = availRow.querySelectorAll("td");

        // columns start at 1 (col 0 is the plan name)
        for (var cIdx = 1; cIdx < availCells.length; cIdx++) {
            var td = availCells[cIdx];
            var tb = td.querySelector("input[type='text'][id$='txtrate']");
            var val = intOrNull(tb ? tb.value : td.textContent);

            if (val === 0) {
                // 1) badge this Availability cell’s input
                if (tb) badgeInput(tb);

                // 2) tint header cell
                if (headerCells[cIdx]) tintTd(headerCells[cIdx]);

                // 3) tint every row’s cell in this column
                for (var r = 1; r < rows.length; r++) {
                    // Restriction rows have their own visual state and must not be
                    // recoloured by the inventory/availability-zero decoration.
                    if (rows[r].classList && rows[r].classList.contains("restriction-grid-row")) continue;
                    var cells = rows[r].querySelectorAll("td");
                    if (cells[cIdx]) tintTd(cells[cIdx]);
                }
            }
        }
    }

    function highlightAllTablesByAvailabilityZero() {
        document.querySelectorAll("div[id^='tablediv_']").forEach(div => {
            highlightByAvailabilityZero(div);
        });
    }

    // Re-run after edits (integrates with your existing handler name)
    function syncOrig(tb) {
        try {
            var cell = tb.closest("td");
            if (cell) {
                var h = cell.querySelector("input[type='hidden'][id$='orig_no_of_rooms']");
                var m = cell.querySelector("span[id$='lbl_orig_no_of_rooms'],label[id$='lbl_orig_no_of_rooms']");
                if (h && m) {
                    var same = (String(tb.value || "").trim() === String(h.value || "").trim());
                    m.innerText = same ? "x" : "";
                    if (same) cell.classList.remove("changed-cell"); else cell.classList.add("changed-cell");
                }
            }
            var div = tb.closest("div[id^='tablediv_']");
            if (div) highlightByAvailabilityZero(div);
        } catch (e) { }
    }

    document.addEventListener("DOMContentLoaded", highlightAllTablesByAvailabilityZero);

    // If you use UpdatePanel
    if (typeof Sys !== "undefined" && Sys.WebForms && Sys.WebForms.PageRequestManager) {
        Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
            setTimeout(highlightAllTablesByAvailabilityZero, 0);
        });
    }
</script>

<script type="text/javascript">
    (function () {
        function getInventoryRoot() {
            return document.getElementById("<%= maindiv.ClientID %>");
        }

        function removeServerIdentityAttributes(node) {
            if (!node) return;

            if (node.removeAttribute) {
                node.removeAttribute("id");
                node.removeAttribute("name");
                node.removeAttribute("for");
            }

            if (node.querySelectorAll) {
                node.querySelectorAll("[id], [name], [for]").forEach(function (el) {
                    el.removeAttribute("id");
                    el.removeAttribute("name");
                    el.removeAttribute("for");
                });
            }
        }

        function getCategoryText(sourceRow) {
            if (!sourceRow || !sourceRow.cells || !sourceRow.cells.length) {
                return "";
            }

            var firstCell = sourceRow.cells[0];
            var visibleLabel = firstCell.querySelector("label");

            return (
                visibleLabel
                    ? visibleLabel.textContent
                    : firstCell.textContent
            ).replace(/\s+/g, " ").trim();
        }

        function decorateCategoryAvailabilityRow(table, categoryText) {
            if (!table) return;

            var availabilityCell = table.querySelector(
                ".calCell[data-rowtype='availability']"
            );

            if (!availabilityCell) return;

            var row = availabilityCell.closest("tr");
            if (!row || !row.cells || !row.cells.length) return;

            // Mark only the category Availability/AVL row so its height can
            // be reduced without changing rate-plan or Net Booking rows.
            row.classList.add("inventory-availability-row");

            var firstCell = row.cells[0];
            if (!firstCell) return;

            var originalLabel = firstCell.querySelector(
                "[id$='plannamelbl']"
            );

            if (originalLabel) {
                originalLabel.classList.add(
                    "inventory-original-availability-label"
                );
            }

            var oldSummary = firstCell.querySelector(
                ".inventory-category-summary"
            );

            if (oldSummary) {
                oldSummary.remove();
            }

            var summary = document.createElement("div");
            summary.className = "inventory-category-summary";

            var name = document.createElement("span");
            name.className = "inventory-category-summary-name";
            name.textContent = categoryText || "Room";

            var avl = document.createElement("span");
            avl.className = "inventory-category-summary-avl";
            avl.textContent = "AVL";

            summary.appendChild(name);
            summary.appendChild(avl);

            firstCell.insertBefore(summary, firstCell.firstChild);
        }

        function buildSingleStickyInventoryHeader() {
            var root = getInventoryRoot();
            if (!root) return;

            root.querySelectorAll(
                ".inventory-sticky-date-header"
            ).forEach(function (el) {
                el.remove();
            });

            var wrappers = Array.from(
                root.querySelectorAll("div[id^='tablediv_']")
            );

            if (!wrappers.length) return;

            var firstTable = wrappers[0].querySelector(
                "table.inventory-category-table, table[id='Table4']"
            );

            if (!firstTable || !firstTable.rows.length) return;

            var sourceRow = firstTable.rows[0];
            if (!sourceRow) return;

            var clonedRow = sourceRow.cloneNode(true);
            removeServerIdentityAttributes(clonedRow);

            if (clonedRow.cells && clonedRow.cells.length) {
                clonedRow.cells[0].innerHTML =
                    '<div class="inventory-sticky-left-caption">Room / Rate Plan</div>';
                clonedRow.cells[0].removeAttribute("style");
            }

            var headerTable = document.createElement("table");
            headerTable.className = "inventory-sticky-date-table";
            headerTable.setAttribute("aria-label", "Inventory date header");

            var tbody = document.createElement("tbody");
            tbody.appendChild(clonedRow);
            headerTable.appendChild(tbody);

            var stickyHeader = document.createElement("div");
            stickyHeader.className = "inventory-sticky-date-header";
            stickyHeader.appendChild(headerTable);

            root.insertBefore(stickyHeader, root.firstChild);

            wrappers.forEach(function (wrapper) {
                wrapper.classList.add("inventory-category-wrap");

                var table = wrapper.querySelector(
                    "table.inventory-category-table, table[id='Table4']"
                );

                if (!table || !table.rows.length) return;

                table.classList.add("inventory-category-table");

                var originalDateRow = table.rows[0];
                var categoryText = getCategoryText(originalDateRow);

                originalDateRow.classList.add(
                    "inventory-source-date-row"
                );

                decorateCategoryAvailabilityRow(
                    table,
                    categoryText
                );
            });
        }

        window.initializeInventorySingleScroll =
            buildSingleStickyInventoryHeader;

        document.addEventListener(
            "DOMContentLoaded",
            buildSingleStickyInventoryHeader
        );

        if (
            window.Sys &&
            Sys.WebForms &&
            Sys.WebForms.PageRequestManager
        ) {
            Sys.WebForms.PageRequestManager
                .getInstance()
                .add_endRequest(function () {
                    window.setTimeout(
                        buildSingleStickyInventoryHeader,
                        0
                    );
                });
        }
    })();
</script>



                </div>

                 <script>
                     function syncScroll(index) {
                         var divid = "tablediv_" + index;
                         var elements = document.querySelectorAll('[id*="tablediv_"]');
                         var tablediv = document.getElementById(divid);


                         tablediv.addEventListener('scroll', function () {
                             elements.forEach(function (element) {
                                 if (element !== tablediv) {
                                     element.scrollLeft = tablediv.scrollLeft;
                                 }
                             });
                         });
                     }
                 </script>
          







                <div class="screenblur" style=" display:none;" id="overlay" runat="server"></div>
                <asp:HiddenField ID="hf_datefdo" runat="server" />
                <asp:HiddenField ID="hf_newcheckindate" runat="server" />

                <asp:Button
                    ID="btnBulkReload"
                    runat="server"
                    Text="reload"
                    OnClick="btnBulkReload_Click"
                    Style="display:none;"
                    UseSubmitBehavior="false" />
            </ContentTemplate>
            <Triggers>
                <asp:AsyncPostBackTrigger
                    ControlID="btnBulkReload"
                    EventName="Click" />
            </Triggers>
        </asp:UpdatePanel>
        <style>
            .greenbuttonstyle
            {
                box-shadow: 0 4px 8px 0 rgba(0, 0, 0, 0.2), 0 6px 20px 0 rgba(0, 0, 0, 0.19);
                border:none; 
                text-align:center;
                padding:10px 0px; 
                text-transform:uppercase;
                font-weight:bold;
                width:150px;
                border-radius:3px;
                color:white;
                background-color:#46bd97; 
                font-size:12px;
            }
            .redbuttonstyle
            {
                box-shadow: 0 4px 8px 0 rgba(0, 0, 0, 0.2), 0 6px 20px 0 rgba(0, 0, 0, 0.19);
                border:none;
                width:150px;
                text-align:center; 
                text-transform:uppercase;
                padding:10px 0px;  
                font-weight:bold; 
                border-radius:3px; 
                color:white;
                background-color:#ef591c; 
                font-size:12px;
            }
            .btn-custom 
            {
                width: 100px;
                padding: 10px 20px;
                background-color: #007bff; /* Bootstrap primary color */
                color: white;
                border: none;
                border-radius: 5px; /* Rounded corners */
                font-size: 16px;
                font-weight: bold;
                text-transform: uppercase;
                box-shadow: 0 4px 8px rgba(0, 0, 0, 0.2); /* Subtle shadow */
                transition: background-color 0.3s, transform 0.3s; /* Smooth transition effects */
            }
            .btn-custom:hover 
            {
                background-color: #0056b3; /* Darker shade on hover */
                transform: translateY(-2px); /* Slightly lift the button on hover */
            }
            .btn-custom:active 
            {
                background-color: #004080; /* Even darker shade when clicked */
                transform: translateY(0); /* Button returns to original position */
            }
        </style>
    </div>          
</div>
<style>
.popup 
{
    display: none;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    height:auto;
    width:70vw;
    position: fixed;
    background-color: #ffffff;
    border: 1px solid #ccc;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.1);
    z-index: 999;
    
}

.popuphk 
{
    display: none;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    height:auto;
    width:600px;
    position: fixed;
    background-color: #ffffff;
    border: 1px solid #ccc;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.1);
    z-index: 999;
    
}

.popup table {
    width: 100%;
    background-color: #e9e9e9;
}

.popup button {
    margin-top: 10px;
    padding: 5px 10px;
    background-color: #4CAF50;
    color: #fff;
    border: none;
    cursor: pointer;
}

.popupalert 
{
    display: none;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    height:200px;
    width:400px;
    position: fixed;
    background-color: #ffffff;
    border: 1px solid #ccc;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.1);
    z-index: 999;
    border-radius:10px;
}

 
.loader-container {
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    display: flex;
    justify-content: center;
    align-items: center;
    background-color: rgba(0, 0, 0, 0.5); /* Semi-transparent background */
    z-index: 1000; /* Ensures the loader is on top */
}

 
.loader {
    border: 8px solid #f3f3f3; /* Light grey background */
    border-top: 8px solid #3498db; /* Blue spinner */
    border-radius: 50%;
    width: 50px;
    height: 50px;
    animation: spin 1s linear infinite; /* Infinite spinning animation */
}

 
@keyframes spin {
    0% { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}
</style>
   <div id="loading" runat="server" style="display:none;">
      <div class="loader-container">
          <div class="loader"></div>
      </div>
  </div>

  <script>
      function clicksavechanges() {
          document.getElementById('<%= loading.ClientID %>').style.display = 'block';
          document.getElementById('<%= Button1.ClientID %>').disabled = true;
          document.getElementById('<%= hiddenbutton.ClientID %>').click();
      }

      function clickbulksavechanges() {
          document.getElementById('<%= loading.ClientID %>').style.display = 'block';
          document.getElementById('<%= Button3.ClientID %>').disabled = true;
          document.getElementById('<%= hiddenbutton2.ClientID %>').click();
      }

      function clickbulksavechanges2() {
          document.getElementById('<%= loading.ClientID %>').style.display = 'block';
          document.getElementById('<%= btnBulkUPloadAvailibilty.ClientID %>').disabled = true;
          document.getElementById('<%= Button7.ClientID %>').click();
      }
      function clickrestrictions() {
          document.getElementById('<%= loading.ClientID %>').style.display = 'block';
          document.getElementById('<%= Button10.ClientID %>').disabled = true;
          document.getElementById('<%= Button11.ClientID %>').click();
      }
      function isNumberKey(evt) {
          var charCode = (evt.which) ? evt.which : evt.keyCode;
          if (charCode > 31 && (charCode < 48 || charCode > 57)) {
              return false;
          }
          return true;
      }
  </script>


    <div runat="server" id="uploadpopup" style="display:none;">
        <div class="popup" style="display:block;">
             <div class="row d-flex justify-content-between" style="padding: 10px; margin-bottom:10px; z-index:999;">
                 <div class=" justify-content-between" style="display:flex;  background-color:#d1e1ef;">
                    <div style=" display:flex; padding-left:10px;">
                        <img src="img/question-mark.png" style=" margin-top:7px; height:15px; width:15px;" />
                        <h4><span style=" vertical-align:middle; font-size:12px; margin-left:5px; font-weight:bold; color:#545454;">Upload Availability</span></h4>
                    </div>
                    <div style="vertical-align:middle;">
                        <asp:ImageButton runat="server" OnClick="closeuploadpopup" ID="ImageButton9" src="img/icons8-cross-50.png" style="vertical-align:bottom; height:20px; width:20px;" />
                    </div>
                </div>
                 <div class="row justify-content-between" style="padding:0px;">
                     <div class="col-md-6 col-12 d-md-block">
                         <label style="font-size:11px; white-space:nowrap; font-weight:bold;">Start Date</label>  
                         <asp:TextBox ID="TextBox1Date" runat="server" textmode="date" CssClass="form-control" Font-Size="11px"></asp:TextBox>
       
                     </div>
                     <div class="col-md-6 col-12 d-md-block">
                         <div runat="server" id="Div1">
                             <label style="font-size:11px; white-space:nowrap; font-weight:bold; width:50px;">End Date</label>  
                             <asp:TextBox ID="TextBox2Date" textmode="date" runat="server" CssClass="form-control" Font-Size="11px" ></asp:TextBox>
      
                            <script type="text/javascript">
                                document.addEventListener("DOMContentLoaded", function () {
                                    var startDateField = document.getElementById('<%= TextBox1Date.ClientID %>');
                                    var endDateField = document.getElementById('<%= TextBox2Date.ClientID %>');

                                    var today = new Date().toISOString().split('T')[0];

                                    // Set both fields to start from today
                                    if (startDateField) startDateField.setAttribute("min", today);
                                    if (endDateField) endDateField.setAttribute("min", today);

                                    // If start date already has a value on load, set end date's min accordingly
                                    if (startDateField && startDateField.value) {
                                        endDateField.setAttribute("min", startDateField.value);
                                    }

                                    // When start date changes, adjust end date's min
                                    if (startDateField && endDateField) {
                                        startDateField.addEventListener('change', function () {
                                            if (startDateField.value) {
                                                var formatted = new Date(startDateField.value).toISOString().split('T')[0];
                                                endDateField.setAttribute("min", formatted);
                                            }
                                        });
                                    }
                                });
                            </script>
                         </div>
                     </div>
                     <div class="col-md-12 col-2 justify-content-center" style=" text-align: center;">
                        <label>&nbsp</label>
                        <asp:Button ID="Button2" runat="server" CssClass="btnform-control" Text="Upload"/>
                    </div>
                     <script type="text/javascript">
                         function isNumberKey(evt) {
                             var charCode = (evt.which) ? evt.which : event.keyCode;
                             if (charCode > 31 && (charCode < 48 || charCode > 57)) {
                                 return false;
                             }
                             return true;
                         }
                     </script>
                 </div>
             </div> 
        </div>
    </div>

     <div runat="server" id="bulkuploadpopup" style="display:none;">
         <div class="popup" style="display:block; overflow:auto; max-height:90vh; max-width:50vw; border-radius:0px;">
              <div class="row d-flex justify-content-between" style="padding: 10px; margin-bottom:10px; z-index:999;">
                  <div class=" justify-content-between" style="display:flex;  background-color:#d1e1ef;">
                     <div style=" display:flex; align-items:end; padding-left:10px;"> 
                         <asp:Label runat="server" ID="heading" style=" vertical-align:middle; font-size:18px; margin-left:5px; font-weight:bold; color:#545454;"></asp:Label>          
                     </div>
                     <div style="vertical-align:middle;">
                         <asp:ImageButton runat="server" OnClick="closebulkuploadpopup" ID="ImageButton1" src="img/icons8-cross-50.png" style="vertical-align:bottom; height:20px; width:20px;" />
                     </div>
                  </div>
                  <div class="row justify-content-between" style="padding:0px;">
                      <div class="col-md-3 col-12 d-md-block">
                          <label style="font-size:11px; white-space:nowrap; font-weight:bold;">Start Date</label>  
                          <asp:TextBox ID="TextBox1" runat="server" TextMode="date" CssClass="form-control" Font-Size="11px" OnTextChanged="gethistory"></asp:TextBox>
   
                          <script type="text/javascript"> 
                              var dateFieldnew = document.getElementById('<%=TextBox1.ClientID %>');
                              var today = new Date().toISOString().split('T')[0];
                              dateFieldnew.setAttribute("min", today);
                          </script>
                      </div>
                      <div class="col-md-3 col-12 d-md-block">
                          <div runat="server" id="Div3">
                              <label style="font-size:11px; white-space:nowrap; font-weight:bold; width:50px;">End Date</label>  
                              <asp:TextBox ID="TextBox2" TextMode="date" runat="server" CssClass="form-control" Font-Size="11px"  OnTextChanged="gethistory"></asp:TextBox>
   
                             <script type="text/javascript">
                                 var dateField1 = document.getElementById('<%=TextBox2.ClientID %>');

                                 var today1 = new Date();
                                 var minDate1 = today1.toISOString().split('T')[0];
                                 dateField1.setAttribute("min", minDate1);

                                 var chkDateField1 = document.getElementById('<%=TextBox1.ClientID %>');
                                 if (chkDateField1.value) {
                                     dateField1.setAttribute("min", chkDateField1.value);
                                 }

                                 chkDateField.addEventListener('change', function () {
                                     var selectedDate1 = new Date(chkDateField1.value);
                                     var selectedDateFormatted1 = selectedDate1.toISOString().split('T')[0];

                                     dateField1.setAttribute("min", selectedDateFormatted1);
                                 });
                             </script>
                          </div>
                      </div>
                      
                      <div class="col-md-3 col-12 d-md-block">
                        <label style="font-size:11px; font-weight:bold;">Category</label>  
                        <asp:DropDownList  style=" width:100%;" ID="DropDownList1" runat="server" CssClass="form-control" Font-Size="11px" OnSelectedIndexChanged="changebulkrepeater" AutoPostBack="true"></asp:DropDownList>
                      </div>
                      <div class="col-md-3 col-12 d-md-block" runat="server" id="rateplanddldiv" style="display:none">
                         <label style="font-size:11px; font-weight:bold;">Rate Plan</label>  
                         <asp:DropDownList  style=" width:100%;" ID="rateplanddl" runat="server" CssClass="form-control" Font-Size="11px" OnSelectedIndexChanged="changebulkrepeaterrateplan" AutoPostBack="true"></asp:DropDownList>
                      </div>
                     <div class="col-md-12 col-12" runat="server" id="daysddldiv">
                          <asp:Label style="white-space: nowrap;" Font-Size="11px" ID="Label2" runat="server">Applicable To</asp:Label>
                          <div class="dropdown-container">
                              <div class="dropdown-field" id="dropdownField">
                                  <div class="selected-tags" id="selectedTags"></div>
                                  <div class="dropdown-arrow" id="dropdownArrow">▼</div>
                              </div> 
                              <div class="dropdown-menu" id="dropdownMenu"></div> 
                              <asp:HiddenField ID="hdnSelectedDays" runat="server" />  
                          </div>
                    </div>
                     
                      <div class="col-md-12 col-12 d-md-block" style="margin-top:15px;">
                          <table style="color:black;">
                            <thead>
                                <tr style="display:flex;">
                                    <th style="display:none;">SR#</th>
                                    <th style="width:300px;">CATEGORY</th>
                                    <th style="width:300px;" runat="server" id="planheading">RATE PLAN</th>
                                    <th style="width:100%;" runat="server" id="rateheading">RATE</th>
                                    <th style="width:100%;" runat="server" id="availableheading">AVAILABILITY</th>
                                </tr>
                            </thead>
                            <tbody>
                                <asp:Repeater runat="server" ID="bulkcategoryraterepeater" OnItemDataBound="applychoice">
                                    <ItemTemplate>
                                        <tr style="display:flex;">
                                            <td style="display:none;">
                                                <asp:HiddenField runat="server" ID="category_id" Value='<%# Eval("category_id") %>' />
                                                <asp:HiddenField runat="server" ID="plan_id" Value='<%# Eval("planid") %>' />
                                            </td>
                                            <td style="width:300px;"><%# Eval("category") %></td> 
                                            <td runat="server" id="planname" style="width:300px; white-space:nowrap;"><%# Eval("planname") %></td> 
                                            <td runat="server" id="ratedata" style="width:100%;"><asp:TextBox runat="server" ID="newrate" style="border: none; border-radius: 0px; width: 100%; text-align:right;" Text='<%# Eval("rate") %>'></asp:TextBox></td> 
                                            <td runat="server" id="availabledata" style="width:100%;"><asp:TextBox runat="server" TextMode="Number" ID="newavailability" style="border: none; border-radius: 0px; width: 100%; text-align:right;" Text='<%# Eval("no_of_rooms") %>'></asp:TextBox></td> 
                                        </tr>
                                    </ItemTemplate>
                                </asp:Repeater>
                            </tbody>
                         </table>
                     </div>
                        <div runat="server" id="ratediv" style="display:none;">
                            <div class="col-md-12 col-12 justify-content-center" style=" text-align: center;">
                                <label>&nbsp</label>
                                <asp:Button ID="Button8" runat="server" OnClientClick="Bulkuploadrate()" CssClass="btnform-control" Text="Upload"/> 
                                <asp:Button ID="Button5" runat="server" OnClick="Bulkuploadrate" CssClass="btnform-control" Text="Upload Rate" Style="display:none;"/> 
                            </div>
                        </div>
                        <div runat="server" id="availablediv" style="display:none;">
                            <div class="col-md-12 col-12 justify-content-center" style=" text-align: center;">
                                <label>&nbsp</label>
                                <asp:Button ID="Button9" runat="server" OnClientClick="Bulkuploadavailability()" CssClass="btnform-control" Text="Upload" /> 
                                <asp:Button ID="Button4" runat="server" OnClick="Bulkuploadavailability" CssClass="btnform-control" Text="Upload Availability" Style="display:none;"/> 
                            </div>
                        </div>
                    
                       <div class="col-md-12 col-12 d-md-block" style="margin:10px 0px;" runat="server" id="historydiv">
                             <h2 style="margin:10px 0px;">Current Rates</h2>
                            <table>
                            <thead>
                                <tr>
                                    <th>SR#</th>
                                    <th>Start Date(dd-MM-yyyy)</th>
                                    <th>End Date(dd-MM-yyyy)</th>
                                    <th>CATEGORY</th>
                                    <th>RATE PLAN</th>
                                    <th>RATE</th> 
                                </tr>
                            </thead>
                            <tbody>
                                <asp:Repeater runat="server" ID="historyrepeater">
                                    <ItemTemplate>
                                        <tr>
                                          
                                            <td><%# Container.ItemIndex + 1 %></td> 
                                            <td><%# DateTime.Parse(Eval("start_date").ToString()).ToString("dd-MM-yyyy") %></td>
                                            <td><%# DateTime.Parse(Eval("end_date").ToString()).ToString("dd-MM-yyyy") %></td> 
                                            <td><%# Eval("category") %></td> 
                                            <td><%# Eval("planname") %></td> 
                                            <td><%# Eval("rate") %></td> 
                                             
                                        </tr>
                                    </ItemTemplate>
                                </asp:Repeater>
                            </tbody>
                        </table>
                    </div>
                    
                      <script type="text/javascript">
                          function Bulkuploadrate() {
                              document.getElementById('<%= loading.ClientID %>').style.display = 'block';
                              document.getElementById('<%= Button8.ClientID %>').disabled = true;
                               document.getElementById('<%= Button5.ClientID %>').click();
                          }
                          function Bulkuploadavailability() {
                                document.getElementById('<%= loading.ClientID %>').style.display = 'block';
                                document.getElementById('<%= Button9.ClientID %>').disabled = true;
                                document.getElementById('<%= Button4.ClientID %>').click();
                          }
                          function isNumberKey(evt) {
                              var charCode = (evt.which) ? evt.which : event.keyCode;
                              if (charCode > 31 && (charCode < 48 || charCode > 57)) {
                                  return false;
                              }
                              return true;
                          }
                      </script>
                      <script type="text/javascript">  
                            var selectedDays = ['All Days'];
                            var isDropdownOpen = false;
                            var allDays = ['All Days', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
                            document.addEventListener("DOMContentLoaded", function () {
                                renderTags();
                                renderDropdownMenu();
                                updateHiddenInput();

                                document.getElementById('dropdownField').addEventListener('click', toggleDropdown);
                                document.addEventListener('click', handleOutsideClick);
                            });
                            function toggleDropdown() {
                                isDropdownOpen = !isDropdownOpen;
                                document.getElementById('dropdownMenu').classList.toggle('show', isDropdownOpen);
                                document.getElementById('dropdownArrow').classList.toggle('open', isDropdownOpen);
                            }
                            function handleOutsideClick(event) {
                                if (!document.querySelector('.dropdown-container').contains(event.target)) {
                                    isDropdownOpen = false;
                                    document.getElementById('dropdownMenu').classList.remove('show');
                                    document.getElementById('dropdownArrow').classList.remove('open');
                                }
                            }
                            function renderTags() {
                                var container = document.getElementById('selectedTags');
                                container.innerHTML = '';

                                selectedDays.forEach(function (day) {
                                    var tag = document.createElement('span');
                                    tag.className = 'tag' + (day === 'Monday' ? ' monday' : '');
                                    tag.innerHTML = day + '<span class="tag-remove" onclick="removeDay(\'' + day + '\')">×</span>';
                                    container.appendChild(tag);
                                });
                            }
                            function renderDropdownMenu() {
                                var menu = document.getElementById('dropdownMenu');
                                menu.innerHTML = '';

                                var available = allDays;

                                available.forEach(function (day) {
                                    var isSelected = selectedDays.includes(day);
                                    var isDisabled = selectedDays.includes('All Days') && day !== 'All Days';

                                    var item = document.createElement('div');
                                    item.className = 'dropdown-item' + (isSelected ? ' selected' : '') + (isDisabled ? ' disabled' : '');
                                    item.innerHTML = '<span>' + day + '</span>' + (isSelected ? '<div class="selected-indicator"></div>' : '');

                                    if (!isDisabled) {
                                        item.onclick = function () {
                                            selectDay(day);
                                        };
                                    }

                                    menu.appendChild(item);
                                });
                            }
                            function selectDay(day) {
                                if (day === 'All Days') {
                                    selectedDays = selectedDays.includes('All Days') ? [] : ['All Days'];
                                } else {
                                    if (selectedDays.includes('All Days')) {
                                        selectedDays = [];
                                    }

                                    var index = selectedDays.indexOf(day);
                                    if (index !== -1) {
                                        selectedDays.splice(index, 1);
                                    } else {
                                        selectedDays.push(day);
                                    }
                                }

                                renderTags();
                                renderDropdownMenu();
                                updateHiddenInput();
                            }
                            function removeDay(day) {
                                var index = selectedDays.indexOf(day);
                                if (index !== -1) {
                                    selectedDays.splice(index, 1);
                                }

                                renderTags();
                                renderDropdownMenu();
                                updateHiddenInput();
                            }
                            function updateHiddenInput() {
                                var input = document.getElementById('<%= hdnSelectedDays.ClientID %>');
                                if (input) {
                                    input.value = selectedDays.join(',');
                                }
                            }

                            function getSelectedDays() {
                                return selectedDays;
                            }
                            function setSelectedDays(days) {
                                selectedDays = typeof days === 'string' ? days.split(',') : days;
                                renderTags();
                                renderDropdownMenu();
                                updateHiddenInput();
                            }
                        </script>
                      <script>
                          function clampAvailabilityInput(inputEl) {
                              var max = parseInt(inputEl.getAttribute("data-max") || inputEl.getAttribute("max") || "0", 10);
                              var v = inputEl.value;
                              if (v === "") return;

                              var n = parseInt(v, 10);
                              if (isNaN(n)) n = 0;

                              if (max > 0 && n > max) n = max;
                              if (n < 0) n = 0;

                              inputEl.value = String(n);
                          }

                          function attachAvailabilityLimiters() {
                              var inputs = document.querySelectorAll('input.avl-limit');
                              inputs.forEach(function (inp) {
                                  if (inp.dataset.bound === "1") return;
                                  inp.dataset.bound = "1";

                                  inp.addEventListener("input", function () { clampAvailabilityInput(inp); });
                                  inp.addEventListener("blur", function () { clampAvailabilityInput(inp); });
                                  inp.addEventListener("paste", function () {
                                      setTimeout(function () { clampAvailabilityInput(inp); }, 0);
                                  });
                              });
                          }

                          document.addEventListener("DOMContentLoaded", attachAvailabilityLimiters);

                          // If this repeater is updated inside UpdatePanel
                          if (window.Sys && Sys.WebForms && Sys.WebForms.PageRequestManager) {
                              Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
                                  attachAvailabilityLimiters();
                              });
                          }
                      </script>

                      <asp:HiddenField runat="server" ID="hfchoice" />
                  </div>
              </div> 
         </div>
     </div>

     <div runat="server" id="restrictionpopup" style="display:none;">
         <div class="popup" style="display:block; overflow:auto; max-height:90vh; max-width:50vw; border-radius:0px;"> 
            <div class="row justify-content-between" style="padding: 10px; margin-bottom:10px; z-index:999; display:flex;  background-color:#d1e1ef;">
                <div class="col-md-10"> 
                    <asp:Label runat="server" ID="Label3" style="vertical-align:middle; font-size:18px; margin-left:5px; font-weight:bold; color:#545454;">Room Restriction</asp:Label>          
                </div>
                <div class="col-md-2 d-flex justify-content-end" style="vertical-align:middle;">
                    <asp:ImageButton runat="server" OnClick="closerestrictionpopup" ID="ImageButton2" src="img/icons8-cross-50.png" style="vertical-align:bottom; height:20px; width:20px;" />
                </div>
            </div> 
            <div class="row">
                <div class="col-md-3 col-12 d-md-block">
                    <label style="font-size:11px; white-space:nowrap; font-weight:bold;">Start Date</label>  
                    <asp:TextBox ID="TextBox3" runat="server" TextMode="date" CssClass="form-control" Font-Size="11px" OnTextChanged="gethistory"></asp:TextBox>
   
                    <script type="text/javascript"> 
                        var dateFieldnew = document.getElementById('<%=TextBox3.ClientID %>');
                        var today = new Date().toISOString().split('T')[0];
                        dateFieldnew.setAttribute("min", today);
                    </script>
                </div>
                <div class="col-md-3 col-12 d-md-block">
                    <div runat="server" id="Div2">
                        <label style="font-size:11px; white-space:nowrap; font-weight:bold; width:50px;">End Date</label>  
                        <asp:TextBox ID="TextBox4" TextMode="date" runat="server" CssClass="form-control" Font-Size="11px"  OnTextChanged="gethistory"></asp:TextBox>
   
                        <script type="text/javascript">
                            var dateField1 = document.getElementById('<%=TextBox4.ClientID %>'); 

                            var today1 = new Date();
                            var minDate1 = today1.toISOString().split('T')[0];
                            dateField1.setAttribute("min", minDate1);

                            var chkDateField1 = document.getElementById('<%=TextBox3.ClientID %>');
                            if (chkDateField1.value) {
                                dateField1.setAttribute("min", chkDateField1.value);
                            }

                            chkDateField.addEventListener('change', function () {
                                var selectedDate1 = new Date(chkDateField1.value);
                                var selectedDateFormatted1 = selectedDate1.toISOString().split('T')[0];

                                dateField1.setAttribute("min", selectedDateFormatted1);
                            });
                        </script>
                    </div>
                </div> 
                <div class="col-md-3 col-12 d-md-block">
                    <label style="font-size:11px; font-weight:bold;">Category</label>  
                    <asp:DropDownList  style=" width:100%;" ID="DropDownList2" runat="server" CssClass="form-control" Font-Size="11px" OnSelectedIndexChanged="changeroomcategoryddl" AutoPostBack="true"></asp:DropDownList>
                </div>
            </div>
             
            <div class="col-md-12 col-12" style="margin-top:15px;">
                <table style="color:black;">
                    <thead>
                        <tr>
                            <th>SR#</th>
                            <th>Category</th>
                            <th>Room</th>  
                            <th>Option</th>
                        </tr>
                    </thead>
                    <tbody>
                        <asp:Repeater runat="server" ID="RoomsRepeater">
                            <ItemTemplate>
                                <tr>
                                    <td><%# Container.ItemIndex+1 %></td>
                                    <td><%# Eval("room_category") %></td> 
                                    <td><%# Eval("room_no") %></td>  
                                    <td style="text-align:center; align-items:center;"> 
                                       <asp:Button 
                                            class="btnstyle" 
                                            style='<%# (Eval("room_status").ToString().Trim() == "Blocked") ? "display:block;" : "display:none;" %>' 
                                            Text="Activate" ID="btndelete" 
                                            CommandArgument='<%# Eval("localroomid") + "/" + Eval("room_category") + "/" + Eval("room_no")  %>' 
                                            OnClick="ActivateRoom" runat="server" /> 

                                        <asp:Button class="btnstyle" 
                                            style='<%# (Eval("room_status").ToString().Trim() != "Blocked") ? "display:block;" : "display:none;"  %>' 
                                            Text="Block" ID="Button2" 
                                            CommandArgument='<%# Eval("localroomid")+"/"+Eval("room_category")+"/"+Eval("room_no") %>' 
                                            OnClick="BlockRoom" runat="server" />
                                    </td>
                                  </tr>
                            </ItemTemplate>
                        </asp:Repeater>
                    </tbody>
                </table>
            </div>
            
         </div>
     </div>
            <!-- Bulk Rate Modal -->
            <asp:HiddenField ID="hfBulkPreviewStart" runat="server" ClientIDMode="Static" />
<asp:HiddenField ID="hfBulkPreviewEnd" runat="server" ClientIDMode="Static" />
<asp:HiddenField ID="hfBulkPreviewCategory" runat="server" ClientIDMode="Static" />
<asp:HiddenField ID="hfBulkPreviewPlan" runat="server" ClientIDMode="Static" />
<asp:HiddenField ID="hfBulkPreviewBaseRate" runat="server" ClientIDMode="Static" />

<div class="modal fade" id="bulkRateModal" tabindex="-1" aria-hidden="true">
  <div class="modal-dialog modal-dialog-centered modal-dialog-scrollable" style="max-width:600px;">
    <div class="modal-content bulkModal">
      <div class="bulkModalHeader">
        <div>
          <div class="bulkTitle" id="bulkModalTitle">Rate / Restriction Update</div>
          <div class="bulkSub" id="bulkRangeText">—</div>
        </div>
        <button type="button" class="bulkClose" data-bs-dismiss="modal" aria-label="Close">×</button>
      </div>

      <div class="modal-body bulkBody">
        <div class="bulkInfoCard">
          <div class="bulkInfoRow">
            <span class="bulkPill" id="bulkCategoryPill">Category</span>
            <span class="bulkPill bulkPill2" id="bulkPlanPill">Plan</span>
          </div>
          <div class="bulkDates" id="bulkDatesPretty">—</div>
        </div>

        <div class="bulkEditorGrid">
          <div class="bulkField" style="margin-top:0;">
            <label class="bulkLabel">Selected Grid Row</label>
            <input type="hidden" id="bulkUpdateType" value="rate" />
            <div id="bulkUpdateName" class="bulkSelectedUpdateName">Rate</div>
            <div class="bulkHint">The update type is taken from the row selected in the main grid.</div>
          </div>

          <div class="bulkCurrentCard d-none">
            <div class="bulkCurrentLabel">Current Value</div>
            <div class="bulkCurrentValue" id="bulkCurrentValue">—</div>
            <div class="bulkCurrentHint" id="bulkCurrentHint">Drag a date range to view existing values.</div>
          </div>
        </div>

        <div id="bulkRateEditor">
          <div class="bulkField">
            <label class="bulkLabel">Base Rate</label>
            <div class="bulkInputWrap">
              <span class="bulkPrefix"></span>
              <input type="number" min="0.01" step="0.01" id="bulkBaseRate" class="form-control"
                     oninput="bulkPreviewRatesDebounced()" />
            </div>
            <div class="bulkHint">Rate functionality remains unchanged. Derived-plan rates are calculated in the existing preview.</div>
          </div>
        </div>

        <div id="bulkRestrictionEditor" style="display:none;">
          <div class="bulkField">
            <label class="bulkLabel" id="bulkRestrictionValueLabel">Restriction Value</label>

            <input type="number" id="bulkRestrictionNumber" class="form-control"
                   min="0" step="1" inputmode="numeric" style="display:none;" />

            <select id="bulkRestrictionBoolean" class="form-select" style="display:none;">
              <option value="true" selected>Closed</option>
              <option value="false">Open</option>
            </select>

            <div id="bulkCutoffEditor" style="display:none;">
              <select id="bulkCutoffMode" class="form-select" onchange="BULK_onCutoffModeChanged()">
                <option value="default">Use Rate Plan Default</option>
                <option value="disabled">Disable for Selected Dates</option>
                <option value="custom">Custom Cutoff Days</option>
              </select>
              <div id="bulkCutoffDaysWrap" style="display:none; margin-top:6px;">
                <input type="number" id="bulkCutoffDays" class="form-control"
                       min="1" max="365" step="1" inputmode="numeric"
                       placeholder="Enter 1 to 365 days" />
              </div>
            </div>

            <div class="bulkHint" id="bulkRestrictionHint">
              Closed blocks the selected action; Open removes that restriction.
            </div>
          </div>
        </div>

        <div class="bulkField">
          <label class="bulkLabel">Apply on Days</label>
          <div class="bulkDays">
            <label><input type="checkbox" class="bulkDay" value="1" checked> Mon</label>
            <label><input type="checkbox" class="bulkDay" value="2" checked> Tue</label>
            <label><input type="checkbox" class="bulkDay" value="3" checked> Wed</label>
            <label><input type="checkbox" class="bulkDay" value="4" checked> Thu</label>
            <label><input type="checkbox" class="bulkDay" value="5" checked> Fri</label>
            <label><input type="checkbox" class="bulkDay" value="6" checked> Sat</label>
            <label><input type="checkbox" class="bulkDay" value="0" checked> Sun</label>
          </div>
        </div>

        <div id="bulkRatePreview" class="mt-3">
          <div style="font-size:13px; margin-bottom:6px;">
            <b>Rate Preview</b>
            <span style="opacity:.7;">(ChangeType + Percentage/Value + New Rate)</span>
          </div>

          <div class="bulkPreviewWrap">
            <table class="table table-sm bulkPreviewTable">
              <thead>
                <tr>
                  <th style="width:35%;font-weight:normal;">Plan</th>
                  <th style="width:15%;font-weight:normal;">Rate</th>
                  <th style="width:20%;font-weight:normal;">ChangeType</th>
                  <th style="width:30%;font-weight:normal;">New Rate</th>
                </tr>
              </thead>
              <tbody id="bulkPreviewTbody">
                <tr><td colspan="4" style="text-align:center; opacity:.7;">—</td></tr>
              </tbody>
            </table>
          </div>
        </div>

        <input type="hidden" id="bulkStart" />
        <input type="hidden" id="bulkEnd" />
        <input type="hidden" id="bulkCategory" />
        <input type="hidden" id="bulkPlan" />
      </div>

      <div class="bulkFooter">
        <button type="button" class="bulkBtn bulkBtnGhost" data-bs-dismiss="modal">Cancel</button>
        <button type="button" class="bulkBtn bulkBtnPrimary" id="bulkSaveButton" onclick="bulkSaveSelected()">Save Rates</button>
      </div>
    </div>
  </div>
</div>
<style>
  .bulkModal{ border-radius:18px; overflow:hidden; border:none; box-shadow:0 18px 50px rgba(0,0,0,.22); }
  .bulkModalHeader{
    display:flex; justify-content:space-between; align-items:flex-start;
    padding:16px 16px 14px;
    background:linear-gradient(135deg, #0e1a2b, #457b9d);
    color:#fff;
  }
  .bulkTitle{ font-size:16px; font-weight:800; letter-spacing:.2px; }
  .bulkSub{ font-size:12px; opacity:.92; margin-top:2px; }
  .bulkClose{
    width:34px; height:34px; border-radius:10px; border:none;
    background: rgba(255,255,255,.18); color:#fff; font-size:20px;
    line-height:34px; text-align:center; cursor:pointer;
  }
  .bulkClose:hover{ background: rgba(255,255,255,.28); }

  .bulkBody{ padding:14px 16px 10px; background:#fff; }
  .bulkInfoCard{
    background: #f5fbfa;
    border: 1px solid rgba(32,185,150,.22);
    border-radius:14px; padding:12px 12px 10px; margin-bottom:12px;
  }
  .bulkInfoRow{ display:flex; gap:8px; flex-wrap:wrap; }
  .bulkPill{
    display:inline-flex; align-items:center;
    background:#e9f7f4; border:1px solid rgba(32,185,150,.25);
    color:#126f60;
    padding:6px 10px; border-radius:999px; font-size:12px; font-weight:700;
  }
  .bulkPill2{ background:#eef4f9; border-color: rgba(69,123,157,.25); color:#2b5872; }
  .bulkDates{ margin-top:8px; font-size:13px; font-weight:800; color:#1f2937; }

  .bulkField{ margin-top:12px; }
  .bulkLabel{ display:block; font-size:12px; font-weight:800; color:#111827; margin-bottom:6px; }
  .bulkInputWrap{ display:flex; align-items:center; border:1px solid #e5e7eb; border-radius:12px; overflow:hidden; }
  .bulkPrefix{
    padding:10px 12px; background:#f9fafb; border-right:1px solid #e5e7eb;
    font-weight:800; color:#374151; font-size:13px;
  }
  .bulkInput{
    width:100%; border:none; outline:none; padding:10px 12px;
    font-size:14px; font-weight:800; color:#111827;
  }
  .bulkHint{ font-size:11px; color:#6b7280; margin-top:6px; }

  .bulkDays{
    display:flex; flex-wrap:wrap; gap:8px;
    background:#fafafa; border:1px solid #eee; padding:10px; border-radius:12px;
  }
  .bulkDays label{
    display:inline-flex; gap:6px; align-items:center;
    padding:6px 10px; border-radius:999px;
    background:#fff; border:1px solid #e5e7eb;
    font-size:12px; font-weight:700; color:#374151;
  }

  .bulkFooter{
    display:flex; justify-content:flex-end; gap:10px;
    padding:12px 16px 14px; background:#fff; border-top:1px solid #f1f5f9;
  }
  .bulkBtn{ border:none; border-radius:12px; padding:10px 14px; font-weight:900; font-size:13px; cursor:pointer; }
  .bulkBtnGhost{ background:#f3f4f6; color:#111827; }
  .bulkBtnGhost:hover{ background:#e5e7eb; }
  .bulkBtnPrimary{ background: linear-gradient(135deg, #26455d, #386583); color:#fff; }
  .bulkBtnPrimary:hover{ filter:brightness(.97); }
  .bulkSelectedUpdateName{ min-height:38px; display:flex; align-items:center; padding:0 12px; border:1px solid #cbd5e1; background:#f8fafc; font-weight:800; color:#0f172a; }
</style>
<style>
  .bulkPreviewWrap { border:1px solid #e6e6e6; border-radius:12px; overflow:hidden; }
  .bulkPreviewTable { margin:0; font-size:13px; }
  .bulkPreviewTable thead th { background:#f7f9fb; font-weight:900; border-bottom:1px solid #e6e6e6; }
  .bulkPreviewTable td, .bulkPreviewTable th { padding:1px; vertical-align:middle; }

  .bulkBadge {
    font-size:12px; padding:3px 8px; border-radius:999px; font-weight:900;
    display:inline-block; background:#eef6ff; color:#1d4ed8;
  }
  .bulkBadgeDerived{ background:#f3f4f6; color:#374151; }

  .adjPlus { color:#16a34a; font-weight:900; }
  .adjMinus { color:#dc2626; font-weight:900; }
  .adjZero { color:#6b7280; font-weight:800; }
</style>
<style>
  .bulkEditorGrid{
    display:grid; grid-template-columns:minmax(240px, 1fr) minmax(240px, 1fr);
    gap:12px; align-items:stretch;
  }
  .bulkCurrentCard{
    border:1px solid #e5e7eb; border-radius:12px; padding:10px 12px;
    background:#fbfdff; min-height:72px;
  }
  .bulkCurrentLabel{ font-size:11px; font-weight:800; color:#6b7280; text-transform:uppercase; letter-spacing:.4px; }
  .bulkCurrentValue{ font-size:16px; font-weight:900; color:#111827; margin-top:3px; }
  .bulkCurrentHint{ font-size:11px; color:#6b7280; margin-top:2px; }
  .bulkRestrictionDetails{
    margin-top:14px; border:1px solid #e5e7eb; border-radius:12px; background:#fff; overflow:hidden;
  }
  .bulkRestrictionDetails > summary{
    cursor:pointer; padding:10px 12px; font-size:12px; font-weight:900;
    background:#f8fafc; color:#1f2937; user-select:none;
  }
  .bulkDetailsLoading{ padding:10px 12px; font-size:12px; color:#6b7280; }
  .bulkDetailsTableWrap{ max-height:260px; overflow:auto; }
  .bulkDetailsTable{ margin:0; font-size:11px; min-width:700px; }
  .bulkDetailsTable thead th{ position:sticky; top:0; z-index:1; background:#eef6f8; white-space:nowrap; }
  .bulkDetailsTable td, .bulkDetailsTable th{ padding:6px 8px !important; vertical-align:middle; }
  .restrictionClosed{ color:#b42318; font-weight:800; }
  .restrictionOpen{ color:#067647; font-weight:800; }
  .restrictionMixed{ color:#b54708; font-weight:800; }
  @media (max-width:767px){
    .bulkEditorGrid{ grid-template-columns:1fr; }
  }
</style>

<style>
  .calCell.bulkSelected { outline: 2px solid #20b996; box-shadow: inset 0 0 0 9999px rgba(32,185,150,.12); }
  .calCell .calInput { pointer-events:none; } /* default */
.calCell .calInput.editing-rate { pointer-events:auto; border:1px solid #cbd5e1; background:#fff !important; }
</style>
<style>
  /* Flat 600px editor requested for the rate/restriction popup. */
  #bulkRateModal .modal-dialog{
      width:600px;
      max-width:calc(100vw - 24px) !important;
  }
  #bulkRateModal .bulkModal,
  #bulkRateModal .bulkInfoCard,
  #bulkRateModal .bulkPill,
  #bulkRateModal .bulkClose,
  #bulkRateModal .bulkInputWrap,
  #bulkRateModal .bulkDays,
  #bulkRateModal .bulkDays label,
  #bulkRateModal .bulkBtn,
  #bulkRateModal .bulkPreviewWrap,
  #bulkRateModal .bulkCurrentCard,
  #bulkRateModal .bulkRestrictionDetails,
  #bulkRateModal .form-control,
  #bulkRateModal .form-select{
      border-radius:0 !important;
  }
  #bulkRateModal .bulkTitle{ font-weight:600; }
  #bulkRateModal .bulkPill,
  #bulkRateModal .bulkDates,
  #bulkRateModal .bulkLabel,
  #bulkRateModal .bulkPrefix,
  #bulkRateModal .bulkInput,
  #bulkRateModal .bulkDays label,
  #bulkRateModal .bulkBtn,
  #bulkRateModal .bulkSelectedUpdateName,
  #bulkRateModal .bulkPreviewTable thead th,
  #bulkRateModal .bulkBadge,
  #bulkRateModal .adjPlus,
  #bulkRateModal .adjMinus,
  #bulkRateModal .adjZero{
      font-weight:500 !important;
  }
  #bulkRateModal .bulkSelectedUpdateName{
      min-height:28px;
      padding:0;
      border:0 !important;
      background:transparent;
  }
</style>
<script type="text/javascript">
    // =============================================================
    // MAIN GRID DRAG EDITOR
    // Rate rows keep their existing behaviour. Restriction rows are rendered
    // under each plan, collapsed by default, and use the same drag editor.
    // =============================================================

    var BULK_isDragging = false;
    var BULK_startCell = null;
    var BULK_didDragMove = false;

    var BULK_catId = "";
    var BULK_planId = "";
    var BULK_catName = "";
    var BULK_planName = "";
    var BULK_rowType = "rate";
    var BULK_canRate = false;
    var BULK_canRestriction = false;

    var BULK_previewTimer = null;
    var BULK_currentData = null;
    var BULK_selectedRateSummary = "—";
    var BULK_selectedRestrictionSummary = "—";
    var BULK_selectedCutoffMode = "default";
    var BULK_selectedCutoffDays = "";

    function BULK_findCellFromTarget(t) {
        while (t && t !== document) {
            if (t.classList && t.classList.contains("calCell")) return t;
            t = t.parentNode;
        }
        return null;
    }

    function BULK_parseYMD(s) {
        var p = (s || "").split("-");
        if (p.length !== 3) return null;
        var y = parseInt(p[0], 10), m = parseInt(p[1], 10), d = parseInt(p[2], 10);
        if (!y || !m || !d) return null;
        return new Date(y, m - 1, d);
    }

    function BULK_formatYMD(dt) {
        var y = dt.getFullYear();
        var m = ("0" + (dt.getMonth() + 1)).slice(-2);
        var d = ("0" + dt.getDate()).slice(-2);
        return y + "-" + m + "-" + d;
    }

    function BULK_prettyDate(ymd) {
        var dt = BULK_parseYMD(ymd);
        if (!dt) return ymd;
        var months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        return dt.getDate() + " " + months[dt.getMonth()] + ", " + dt.getFullYear();
    }

    function BULK_escapeHtml(value) {
        return (value == null ? "" : String(value))
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    function BULK_clearSelection() {
        document.querySelectorAll(".calCell.bulkSelected").forEach(function (el) {
            el.classList.remove("bulkSelected");
        });
    }

    function BULK_setText(id, text) {
        var el = document.getElementById(id);
        if (el) el.textContent = text == null ? "" : text;
    }

    function BULK_resetModalFields() {
        ["bulkStart", "bulkEnd", "bulkCategory", "bulkPlan"].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.value = "";
        });

        var base = document.getElementById("bulkBaseRate");
        if (base) base.value = "";

        var numberInput = document.getElementById("bulkRestrictionNumber");
        if (numberInput) numberInput.value = "";

        var boolSelect = document.getElementById("bulkRestrictionBoolean");
        if (boolSelect) boolSelect.value = "true"; // default Closed

        var cutoffMode = document.getElementById("bulkCutoffMode");
        if (cutoffMode) cutoffMode.value = "default";

        var cutoffDays = document.getElementById("bulkCutoffDays");
        if (cutoffDays) cutoffDays.value = "";

        BULK_selectedCutoffMode = "default";
        BULK_selectedCutoffDays = "";

        var updateType = document.getElementById("bulkUpdateType");
        if (updateType) updateType.value = "rate";

        var tbody = document.getElementById("bulkPreviewTbody");
        if (tbody) tbody.innerHTML = '<tr><td colspan="4" style="text-align:center; opacity:.7;">—</td></tr>';

        BULK_setText("bulkCategoryPill", "Category");
        BULK_setText("bulkPlanPill", "Plan");
        BULK_setText("bulkDatesPretty", "—");
        BULK_setText("bulkRangeText", "—");
        BULK_setText("bulkCurrentValue", "—");
        BULK_setText("bulkCurrentHint", "Drag a date range to view existing values.");
        BULK_currentData = null;
        BULK_selectedRateSummary = "—";
        BULK_selectedRestrictionSummary = "—";
        BULK_rowType = "rate";
        BULK_setText("bulkUpdateName", "Rate");
        BULK_onUpdateTypeChanged();
    }

    function BULK_fullClear() {
        BULK_isDragging = false;
        BULK_startCell = null;
        BULK_didDragMove = false;
        BULK_clearSelection();
        BULK_resetModalFields();
        BULK_catId = "";
        BULK_planId = "";
        BULK_catName = "";
        BULK_planName = "";
        BULK_rowType = "rate";
        BULK_canRate = false;
        BULK_canRestriction = false;
    }

    function BULK_openModalSafe() {
        var el = document.getElementById("bulkRateModal");
        if (window.bootstrap && bootstrap.Modal && el) {
            bootstrap.Modal.getOrCreateInstance(el).show();
            return;
        }
        if (window.jQuery && el && typeof window.jQuery(el).modal === "function") {
            window.jQuery(el).modal("show");
            return;
        }
        alert("Bootstrap JS not loaded");
    }

    function BULK_closeModalSafe() {
        var el = document.getElementById("bulkRateModal");
        if (window.bootstrap && bootstrap.Modal && el) {
            var instance = bootstrap.Modal.getInstance(el);
            if (instance) instance.hide();
            return;
        }
        var closeBtn = document.querySelector("#bulkRateModal [data-bs-dismiss='modal']");
        if (closeBtn) closeBtn.click();
    }

    function canUpdateRate(cell) {
        return !!cell &&
            (cell.getAttribute("data-canupdaterate") || "0") === "1" &&
            (cell.getAttribute("data-derivedlocked") || "0") !== "1";
    }

    function canBulkUpdateRate(cell) {
        return !!cell &&
            (cell.getAttribute("data-canbulkrateupdate") || "0") === "1" &&
            (cell.getAttribute("data-derivedlocked") || "0") !== "1";
    }

    function canUpdateRestriction(cell) {
        return !!cell && (cell.getAttribute("data-canupdaterestriction") || "0") === "1";
    }

    function BULK_isRestrictionType(type) {
        return type === "closed_to_arrival" ||
               type === "closed_to_departure" ||
               type === "max_stay" ||
               type === "min_stay_arrival" ||
               type === "min_stay_through" ||
               type === "booking_cutoff" ||
               type === "stop_sell";
    }

    function BULK_setSelectedUpdateType(type) {
        BULK_rowType = type || "rate";
        var field = document.getElementById("bulkUpdateType");
        if (field) field.value = BULK_rowType;
        BULK_setText("bulkUpdateName", BULK_labelForType(BULK_rowType));
    }

    function BULK_rateSummaryFromCells(cells) {
        var values = [];
        cells.forEach(function (cell) {
            var input = cell.querySelector("input[type='text'][id$='txtrate']");
            var value = input ? (input.value || "").trim() : "";
            if (value !== "" && values.indexOf(value) < 0) values.push(value);
        });

        if (values.length === 0) return "—";
        if (values.length === 1) return values[0];
        return "Mixed (" + values.length + " values)";
    }

    function BULK_restrictionSummaryFromCells(cells) {
        var values = [];
        cells.forEach(function (cell) {
            var raw = (cell.getAttribute("data-current") || "").trim();
            var shown = (cell.textContent || "").trim();
            var rowType = (cell.getAttribute("data-rowtype") || "").trim();
            var value = rowType === "booking_cutoff"
                ? (shown || raw || "—")
                : (raw !== "" ? raw : (shown || "—"));
            if (value !== "" && values.indexOf(value) < 0) values.push(value);
        });
        if (values.length === 0) return "—";
        if (values.length === 1) {
            if (values[0] === "true") return "Closed";
            if (values[0] === "false") return "Open";
            return values[0];
        }
        return "Mixed (" + values.length + " values)";
    }

    function BULK_captureCutoffSelection(cells) {
        var modes = [];
        var days = [];
        (cells || []).forEach(function (cell) {
            var mode = (cell.getAttribute("data-cutoff-mode") || "").trim().toLowerCase();
            var dayValue = (cell.getAttribute("data-cutoff-days") || "").trim();
            if (mode && modes.indexOf(mode) < 0) modes.push(mode);
            if (dayValue && days.indexOf(dayValue) < 0) days.push(dayValue);
        });
        BULK_selectedCutoffMode = modes.length === 1 ? modes[0] : "default";
        BULK_selectedCutoffDays = days.length === 1 ? days[0] : "";
    }

    var BULK_openGroupsStorageKey =
        "AvailabilitySetup.OpenRestrictionGroups.<%= System.Web.HttpUtility.JavaScriptStringEncode(Convert.ToString(_hotelId)) %>";

    function BULK_readOpenGroups() {
        try {
            var raw = sessionStorage.getItem(BULK_openGroupsStorageKey);
            var parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch (ignore) {
            return [];
        }
    }

    function BULK_writeOpenGroups(groups) {
        try {
            sessionStorage.setItem(
                BULK_openGroupsStorageKey,
                JSON.stringify(Array.from(new Set(groups || [])))
            );
        } catch (ignore) { }
    }

    function BULK_applyRestrictionGroupState(group, opening) {
        if (!group) return;

        var root = document.getElementById("<%= maindiv.ClientID %>") || document;
        var rows = root.querySelectorAll("tr.restriction-grid-row");
        rows.forEach(function (row) {
            if ((row.getAttribute("data-restriction-group") || "") === group) {
                row.style.display = opening ? "table-row" : "none";
            }
        });

        var buttons = root.querySelectorAll(".restriction-toggle");
        buttons.forEach(function (button) {
            if ((button.getAttribute("data-group") || "") === group) {
                button.classList.toggle("is-open", opening);
                button.setAttribute("aria-expanded", opening ? "true" : "false");
            }
        });
    }

    function BULK_rememberRestrictionGroup(group, opening) {
        if (!group) return;

        var groups = BULK_readOpenGroups();
        var index = groups.indexOf(group);

        if (opening && index < 0) groups.push(group);
        if (!opening && index >= 0) groups.splice(index, 1);

        BULK_writeOpenGroups(groups);
        BULK_applyRestrictionGroupState(group, opening);
    }

    function BULK_restoreRestrictionGroups() {
        BULK_readOpenGroups().forEach(function (group) {
            BULK_applyRestrictionGroupState(group, true);
        });
    }

    function BULK_toggleRestrictionGroup(button) {
        if (!button) return;

        var group = button.getAttribute("data-group") || "";
        if (!group) return;

        var opening = !button.classList.contains("is-open");
        BULK_rememberRestrictionGroup(group, opening);
    }

    function BULK_openSingleRestriction(cell) {
        if (!cell) return;

        var rowType = cell.getAttribute("data-rowtype") || "";
        if (!BULK_isRestrictionType(rowType) || !canUpdateRestriction(cell)) return;

        var categoryId = cell.getAttribute("data-category") || "";
        var planId = cell.getAttribute("data-plan") || "";
        var date = cell.getAttribute("data-date") || "";
        if (!categoryId || !planId || !date) return;

        BULK_catId = categoryId;
        BULK_planId = planId;
        BULK_rowType = rowType;
        BULK_catName = (cell.getAttribute("data-categoryname") || "Category").trim();
        BULK_planName = (cell.getAttribute("data-planname") || "Plan").replace("(Rate)", "").trim();
        BULK_canRate = false;
        BULK_canRestriction = true;

        BULK_clearSelection();
        cell.classList.add("bulkSelected");

        document.getElementById("bulkStart").value = date;
        document.getElementById("bulkEnd").value = date;
        document.getElementById("bulkCategory").value = categoryId;
        document.getElementById("bulkPlan").value = planId;

        BULK_setText("bulkCategoryPill", BULK_catName);
        BULK_setText("bulkPlanPill", BULK_planName);
        BULK_setText("bulkDatesPretty", BULK_prettyDate(date));
        BULK_setText("bulkRangeText", date);

        BULK_selectedRateSummary = "—";
        BULK_selectedRestrictionSummary = BULK_restrictionSummaryFromCells([cell]);
        if (rowType === "booking_cutoff") BULK_captureCutoffSelection([cell]);

        BULK_setSelectedUpdateType(rowType);
        BULK_onUpdateTypeChanged();

        // Keep this plan's restrictions visible before and after saving.
        BULK_rememberRestrictionGroup(categoryId + "||" + planId, true);
        BULK_openModalSafe();
    }

    function BULK_bindDrag() {
        var root = document.getElementById("<%= maindiv.ClientID %>") || document;
        if (root.__bulkBound === true) return;
        root.__bulkBound = true;

        root.addEventListener("click", function (e) {
            var button = e.target && e.target.closest ? e.target.closest(".restriction-toggle") : null;
            if (!button) return;
            BULK_toggleRestrictionGroup(button);
            e.preventDefault();
            e.stopPropagation();
        }, true);

        root.addEventListener("dblclick", function (e) {
            var cell = BULK_findCellFromTarget(e.target);
            if (!cell) return;

            var rowType = cell.getAttribute("data-rowtype") || "";

            // Rate double-click remains handled by the existing inline rate editor.
            if (rowType === "rate") return;

            // A restriction cell can now be updated for one date by double-clicking it.
            if (!BULK_isRestrictionType(rowType)) return;

            if (!canUpdateRestriction(cell)) {
                e.preventDefault();
                e.stopPropagation();
                return;
            }

            BULK_openSingleRestriction(cell);
            e.preventDefault();
            e.stopPropagation();
        }, true);

        root.addEventListener("mousedown", function (e) {
            if (e.target && e.target.closest && e.target.closest(".restriction-toggle")) return;

            var cell = BULK_findCellFromTarget(e.target);
            if (!cell) return;

            var rowType = cell.getAttribute("data-rowtype") || "";
            if (rowType !== "rate" && !BULK_isRestrictionType(rowType)) return;

            // Dragging a price/rate row is controlled only by BulkRateUpdate.
            if (rowType === "rate" && !canBulkUpdateRate(cell)) return;
            if (rowType !== "rate" && !canUpdateRestriction(cell)) return;

            // Do not block bulk dragging just because the single-day editor is open.
            // A click still edits one day; actual mouse movement remains the bulk gesture.
            var tb = cell.querySelector("input[type='text'][id$='txtrate']");

            var cat = cell.getAttribute("data-category") || "";
            var plan = cell.getAttribute("data-plan") || "";
            var date = cell.getAttribute("data-date") || "";
            if (!cat || !plan || !date) return;

            BULK_isDragging = true;
            BULK_startCell = cell;
            BULK_didDragMove = false;
            BULK_catId = cat;
            BULK_planId = plan;
            BULK_rowType = rowType;
            BULK_catName = (cell.getAttribute("data-categoryname") || "Category").trim();
            BULK_planName = (cell.getAttribute("data-planname") || "Plan").replace("(Rate)", "").trim();
            BULK_canRate = rowType === "rate" && canBulkUpdateRate(cell);
            BULK_canRestriction = rowType !== "rate" && canUpdateRestriction(cell);

            BULK_clearSelection();
            cell.classList.add("bulkSelected");
            e.preventDefault();
        }, true);

        root.addEventListener("mouseover", function (e) {
            if (!BULK_isDragging || !BULK_startCell) return;

            var cell = BULK_findCellFromTarget(e.target);
            if (!cell || (cell.getAttribute("data-rowtype") || "") !== BULK_rowType) return;

            var cat = cell.getAttribute("data-category") || "";
            var plan = cell.getAttribute("data-plan") || "";
            if (cat !== BULK_catId || plan !== BULK_planId) return;

            if (cell !== BULK_startCell) BULK_didDragMove = true;
            cell.classList.add("bulkSelected");
        }, true);

        if (!document.__bulkMouseUpBound) {
            document.__bulkMouseUpBound = true;
            document.addEventListener("mouseup", BULK_finishDrag, true);
        }
    }

    function BULK_openRateCellsForIndividualEditing(selected) {
        var opened = [];

        (selected || []).forEach(function (cell) {
            if (!cell || !canUpdateRate(cell)) return;

            var tb = cell.querySelector("input[type='text'][id$='txtrate']");
            if (!tb) return;

            if (typeof unlockTB === "function") {
                unlockTB(tb, false);
            } else {
                if (tb.dataset.originalRate === undefined) {
                    tb.dataset.originalRate = (tb.value || "").trim();
                }
                tb.readOnly = false;
                tb.classList.add("editing-rate");
                tb.style.pointerEvents = "auto";
            }

            cell.classList.add("individual-rate-open");
            opened.push(tb);
        });

        // Rate dragging is no longer a bulk-rate operation.
        // It simply opens the selected dates so every day can have a different price.
        BULK_clearSelection();

        if (opened.length > 0) {
            window.setTimeout(function () {
                try {
                    opened[0].focus();
                    opened[0].select();
                } catch (ignore) { }
            }, 0);
        }

        if (typeof updateSaveRatesButtonState === "function") {
            updateSaveRatesButtonState();
        }
    }

    function BULK_finishDrag() {
        if (!BULK_isDragging) return;
        BULK_isDragging = false;

        if (!BULK_didDragMove) {
            BULK_clearSelection();
            BULK_startCell = null;
            return;
        }

        // A completed rate drag is a bulk action, not a single-cell click.
        // Suppress the click event that browsers dispatch immediately after mouseup.
        if (BULK_rowType === "rate") {
            window.__suppressRateCellClickUntil = Date.now() + 300;
        }

        var selected = Array.from(document.querySelectorAll(".calCell.bulkSelected"))
            .filter(function (cell) {
                return (cell.getAttribute("data-category") || "") === BULK_catId &&
                       (cell.getAttribute("data-plan") || "") === BULK_planId &&
                       (cell.getAttribute("data-rowtype") || "") === BULK_rowType;
            });

        if (selected.length === 0) {
            BULK_startCell = null;
            return;
        }

        var minD = null, maxD = null;
        selected.forEach(function (cell) {
            var d = BULK_parseYMD(cell.getAttribute("data-date"));
            if (!d) return;
            if (!minD || d < minD) minD = d;
            if (!maxD || d > maxD) maxD = d;
        });

        if (!minD || !maxD) {
            BULK_startCell = null;
            return;
        }

        var start = BULK_formatYMD(minD);
        var end = BULK_formatYMD(maxD);
        document.getElementById("bulkStart").value = start;
        document.getElementById("bulkEnd").value = end;
        document.getElementById("bulkCategory").value = BULK_catId;
        document.getElementById("bulkPlan").value = BULK_planId;

        BULK_setText("bulkCategoryPill", BULK_catName);
        BULK_setText("bulkPlanPill", BULK_planName);
        BULK_setText("bulkDatesPretty", BULK_prettyDate(start) + " → " + BULK_prettyDate(end));
        BULK_setText("bulkRangeText", start + " to " + end);

        if (BULK_rowType === "rate") {
            // Restore the original drag behaviour:
            // a permitted rate drag opens the Bulk Rate Update popup.
            BULK_selectedRateSummary = BULK_rateSummaryFromCells(selected);
            BULK_selectedRestrictionSummary = "—";
        } else {
            BULK_selectedRestrictionSummary = BULK_restrictionSummaryFromCells(selected);
            BULK_selectedRateSummary = "—";
            if (BULK_rowType === "booking_cutoff") BULK_captureCutoffSelection(selected);
        }

        BULK_setSelectedUpdateType(BULK_rowType);
        BULK_onUpdateTypeChanged();
        BULK_openModalSafe();
        BULK_startCell = null;
    }

    function BULK_isBooleanRestriction(type) {
        return type === "closed_to_arrival" ||
               type === "closed_to_departure" ||
               type === "stop_sell";
    }

    function BULK_labelForType(type) {
        var labels = {
            rate: "Rate",
            closed_to_arrival: "Closed To Arrival",
            closed_to_departure: "Closed To Departure",
            max_stay: "Max Stay",
            min_stay_arrival: "Min Stay Arrival",
            min_stay_through: "Min Stay Through",
            booking_cutoff: "Booking Cutoff",
            stop_sell: "Stop Sell"
        };
        return labels[type] || type;
    }

    function BULK_getCurrentSummary(type) {
        if (type === "rate") return BULK_selectedRateSummary || "—";
        return BULK_selectedRestrictionSummary || "—";
    }

    function BULK_onCutoffModeChanged() {
        var mode = (document.getElementById("bulkCutoffMode") || {}).value || "default";
        var wrap = document.getElementById("bulkCutoffDaysWrap");
        var days = document.getElementById("bulkCutoffDays");

        if (wrap) wrap.style.display = mode === "custom" ? "block" : "none";
        if (days) {
            days.disabled = mode !== "custom";
            if (mode !== "custom") days.value = "";
        }

        if (mode === "default") {
            BULK_setText("bulkRestrictionHint", "Use the Booking Cutoff configured in Rate Plan Maker for this rate plan.");
        } else if (mode === "disabled") {
            BULK_setText("bulkRestrictionHint", "Disable automatic cutoff for the selected dates only. Manual Stop Sell remains unchanged.");
        } else {
            BULK_setText("bulkRestrictionHint", "Enter a whole number from 1 to 365. The rate plan closes when arrival is fewer than this many days away.");
        }
    }

    function BULK_onUpdateTypeChanged() {
        var ddl = document.getElementById("bulkUpdateType");
        if (!ddl) return;

        var type = ddl.value || "rate";
        BULK_setText("bulkUpdateName", BULK_labelForType(type));
        var isRate = type === "rate";
        var isBool = BULK_isBooleanRestriction(type);
        var isCutoff = type === "booking_cutoff";

        var rateEditor = document.getElementById("bulkRateEditor");
        var restrictionEditor = document.getElementById("bulkRestrictionEditor");
        var ratePreview = document.getElementById("bulkRatePreview");
        var num = document.getElementById("bulkRestrictionNumber");
        var bool = document.getElementById("bulkRestrictionBoolean");
        var cutoffEditor = document.getElementById("bulkCutoffEditor");
        var cutoffMode = document.getElementById("bulkCutoffMode");
        var cutoffDays = document.getElementById("bulkCutoffDays");
        var save = document.getElementById("bulkSaveButton");

        if (rateEditor) rateEditor.style.display = isRate ? "block" : "none";
        if (ratePreview) ratePreview.style.display = isRate ? "block" : "none";
        if (restrictionEditor) restrictionEditor.style.display = isRate ? "none" : "block";
        if (num) num.style.display = (!isRate && !isBool && !isCutoff) ? "block" : "none";
        if (bool) bool.style.display = (!isRate && isBool) ? "block" : "none";
        if (cutoffEditor) cutoffEditor.style.display = isCutoff ? "block" : "none";

        if (isCutoff) {
            if (cutoffMode) cutoffMode.value = BULK_selectedCutoffMode || "default";
            if (cutoffDays) cutoffDays.value = BULK_selectedCutoffDays || "";
            BULK_onCutoffModeChanged();
        }

        BULK_setText("bulkCurrentValue", BULK_getCurrentSummary(type));
        BULK_setText("bulkCurrentHint", isRate
            ? "Current rates are read directly from the selected rate cells."
            : "Current restriction values are shown directly in the selected main-grid row.");

        if (save) save.textContent = isRate ? "Save Rates" : "Save Restriction";
        BULK_setText("bulkModalTitle", isRate ? "Bulk Rate Update" : "Bulk Restriction Update");
        BULK_setText("bulkRestrictionValueLabel", BULK_labelForType(type) + " Value");

        if (!isRate && isBool) {
            if (bool) bool.value = "true";
            BULK_setText("bulkRestrictionHint", "Closed applies the manual restriction. Open removes it for the selected plan, category and dates.");
        } else if (!isRate && !isCutoff) {
            if (num) {
                num.min = type === "max_stay" ? "0" : "1";
                num.placeholder = type === "max_stay" ? "0 = no maximum restriction" : "Minimum 1 night";
            }
            BULK_setText("bulkRestrictionHint", type === "max_stay"
                ? "Enter 0 to remove the maximum-stay restriction."
                : "Enter the required number of nights. Minimum value is 1.");
        }
    }

    function BULK_boolBadge(value) {
        if (value === "Closed") return '<span class="restrictionClosed">Closed</span>';
        if (value === "Open") return '<span class="restrictionOpen">Open</span>';
        if (value === "Mixed") return '<span class="restrictionMixed">Mixed</span>';
        return BULK_escapeHtml(value || "—");
    }

    async function BULK_loadRestrictionDetails() {
        var start = (document.getElementById("bulkStart") || {}).value || "";
        var end = (document.getElementById("bulkEnd") || {}).value || "";
        var categoryId = (document.getElementById("bulkCategory") || {}).value || "";
        var planId = (document.getElementById("bulkPlan") || {}).value || "";
        if (!start || !end || !categoryId || !planId) return;

        BULK_setText("bulkDetailsLoading", "Loading restriction details...");

        try {
            var url = window.location.pathname.replace(/\/+$/, "") + "/LoadRestrictionRange";
            var response = await fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json; charset=utf-8" },
                body: JSON.stringify({ req: {
                    categoryId: categoryId,
                    planId: planId,
                    start: start,
                    end: end
                }})
            });

            var raw = await response.text();
            if (raw.trim().charAt(0) === "<") throw new Error("Server returned HTML. Session may have expired.");

            var json = JSON.parse(raw);
            var data = json.d;
            if (!data || data.ok !== true) throw new Error((data && (data.message || data.error)) || "Unable to load restrictions.");

            BULK_currentData = data;
            BULK_setText("bulkDetailsLoading", (data.rows || []).length + " date(s) loaded on demand.");
            BULK_renderRestrictionDetails(data.rows || []);
            BULK_onUpdateTypeChanged();
        } catch (error) {
            BULK_currentData = null;
            BULK_setText("bulkDetailsLoading", "Restriction details could not be loaded: " + error.message);
            var body = document.getElementById("bulkRestrictionDetailsBody");
            if (body) body.innerHTML = '<tr><td colspan="7" style="text-align:center; color:#dc2626;">' + BULK_escapeHtml(error.message) + '</td></tr>';
            BULK_onUpdateTypeChanged();
        }
    }

    function BULK_renderRestrictionDetails(rows) {
        var body = document.getElementById("bulkRestrictionDetailsBody");
        if (!body) return;

        if (!rows || rows.length === 0) {
            body.innerHTML = '<tr><td colspan="7" style="text-align:center; opacity:.7;">No rows</td></tr>';
            return;
        }

        var html = "";
        rows.forEach(function (r) {
            html += "<tr>"
                + "<td>" + BULK_escapeHtml(r.date || "") + "</td>"
                + "<td>" + BULK_escapeHtml(r.min_stay_arrival || "—") + "</td>"
                + "<td>" + BULK_escapeHtml(r.min_stay_through || "—") + "</td>"
                + "<td>" + BULK_escapeHtml(r.max_stay || "—") + "</td>"
                + "<td>" + BULK_boolBadge(r.closed_to_arrival) + "</td>"
                + "<td>" + BULK_boolBadge(r.closed_to_departure) + "</td>"
                + "<td>" + BULK_boolBadge(r.stop_sell) + "</td>"
                + "</tr>";
        });
        body.innerHTML = html;
    }

    function BULK_money(cur, value) {
        if (value == null || value === "" || isNaN(value)) return "—";
        return (cur ? (cur + " ") : "") + Number(value).toFixed(2);
    }

    function BULK_adjClass(adj) {
        var n = Number(adj);
        if (!isFinite(n) || n === 0) return "adjZero";
        return n > 0 ? "adjPlus" : "adjMinus";
    }

    function BULK_previewRatesDebounced() {
        if (BULK_previewTimer) clearTimeout(BULK_previewTimer);
        BULK_previewTimer = setTimeout(BULK_previewRates, 250);
    }

    async function BULK_previewRates() {
        try {
            var start = (document.getElementById("bulkStart") || {}).value || "";
            var end = (document.getElementById("bulkEnd") || {}).value || "";
            var categoryId = (document.getElementById("bulkCategory") || {}).value || "";
            var planId = (document.getElementById("bulkPlan") || {}).value || "";
            var baseRate = parseFloat(((document.getElementById("bulkBaseRate") || {}).value || "0"));
            var tbody = document.getElementById("bulkPreviewTbody");
            if (!tbody) return;

            if (!start || !end || !categoryId || !planId || !(baseRate > 0)) {
                tbody.innerHTML = '<tr><td colspan="4" style="text-align:center; opacity:.7;">—</td></tr>';
                return;
            }

            var days = Array.from(document.querySelectorAll(".bulkDay:checked"))
                .map(function (x) { return parseInt(x.value, 10); });

            var payload = {
                hotelId: "<%= _hotelId %>",
                rooms: [categoryId],
                plans: [planId],
                days: days,
                ranges: [{ start: start, end: end, baseRate: baseRate }]
            };

            var url = window.location.pathname.replace(/\/+$/, "") + "/PreviewBulkRate";
            var resp = await fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json; charset=utf-8" },
                body: JSON.stringify({ req: payload })
            });

            var raw = await resp.text();
            if (raw.trim().charAt(0) === "<") {
                tbody.innerHTML = '<tr><td colspan="4" style="text-align:center; color:#dc2626;">Preview failed (HTML response)</td></tr>';
                return;
            }

            var json = JSON.parse(raw);
            var data = json.d;
            if (!data || !data.ok) {
                var msg = (data && (data.message || data.error)) ? (data.message || data.error) : "Preview failed";
                tbody.innerHTML = '<tr><td colspan="4" style="text-align:center; color:#dc2626;">' + BULK_escapeHtml(msg) + '</td></tr>';
                return;
            }

            var rows = data.rows || [];
            if (rows.length === 0) {
                tbody.innerHTML = '<tr><td colspan="4" style="text-align:center; opacity:.7;">No preview rows</td></tr>';
                return;
            }

            var html = "";
            rows.forEach(function (r) {
                var badge = r.isParent
                    ? '<span class="bulkBadge">Parent</span>'
                    : '<span class="bulkBadge bulkBadgeDerived">Derived</span>';

                var changeType = r.changeType || "";
                var adj = r.adjustment != null ? r.adjustment : 0;
                var adjText = "—";
                if (changeType.toLowerCase() === "percentage") {
                    adjText = (adj >= 0 ? "+" : "") + Number(adj).toFixed(0) + "%";
                } else if (changeType.toLowerCase() === "value") {
                    adjText = (adj >= 0 ? "+" : "") + Number(adj).toFixed(2);
                }

                html += "<tr>"
                    + "<td><div style='display:flex; gap:8px; align-items:center;'>" + badge
                    + "<span style='font-weight:normal;'>" + BULK_escapeHtml(r.planText || "") + "</span></div></td>"
                    + "<td class='" + BULK_adjClass(adj) + "'>" + BULK_escapeHtml(adjText) + "</td>"
                    + "<td>" + BULK_escapeHtml(changeType || "—") + "</td>"
                    + "<td style='text-align:right;'>" + BULK_escapeHtml(BULK_money(r.currency || "", r.newRate)) + "</td>"
                    + "</tr>";
            });
            tbody.innerHTML = html;
        } catch (error) {
            var tbody2 = document.getElementById("bulkPreviewTbody");
            if (tbody2) tbody2.innerHTML = '<tr><td colspan="4" style="text-align:center; color:#dc2626;">Preview error</td></tr>';
        }
    }

    async function bulkSaveRates() {
        var start = (document.getElementById("bulkStart") || {}).value || "";
        var end = (document.getElementById("bulkEnd") || {}).value || "";
        var categoryId = (document.getElementById("bulkCategory") || {}).value || "";
        var planId = (document.getElementById("bulkPlan") || {}).value || "";
        var baseRate = parseFloat(((document.getElementById("bulkBaseRate") || {}).value || "0"));

        if (!BULK_canRate) { alert("You do not have permission to update rates."); return; }
        if (!start || !end || !categoryId || !planId) { alert("Select a range first."); return; }
        if (!(baseRate > 0)) { alert("Enter Base Rate"); return; }

        var days = Array.from(document.querySelectorAll(".bulkDay:checked"))
            .map(function (x) { return parseInt(x.value, 10); });
        if (days.length === 0) { alert("Select at least one day."); return; }

        var payload = {
            hotelId: "<%= _hotelId %>",
            rooms: [categoryId],
            plans: [planId],
            days: days,
            ranges: [{ start: start, end: end, baseRate: baseRate }]
        };

        await BULK_postSave("SaveBulkRates", { req: payload });
    }

    async function BULK_saveRestriction() {
        if (!BULK_canRestriction) { alert("You do not have permission to update restrictions."); return; }

        var type = (document.getElementById("bulkUpdateType") || {}).value || "";
        var start = (document.getElementById("bulkStart") || {}).value || "";
        var end = (document.getElementById("bulkEnd") || {}).value || "";
        var categoryId = (document.getElementById("bulkCategory") || {}).value || "";
        var planId = (document.getElementById("bulkPlan") || {}).value || "";
        if (!type || type === "rate" || !start || !end || !categoryId || !planId) {
            alert("Select a restriction and date range.");
            return;
        }

        var value;
        if (BULK_isBooleanRestriction(type)) {
            value = (document.getElementById("bulkRestrictionBoolean") || {}).value || "true";
        } else if (type === "booking_cutoff") {
            var cutoffMode = (document.getElementById("bulkCutoffMode") || {}).value || "default";

            if (cutoffMode === "default") {
                value = "default";
            } else if (cutoffMode === "disabled") {
                value = "disabled";
            } else {
                var cutoffDays = ((document.getElementById("bulkCutoffDays") || {}).value || "").trim();
                var parsedCutoffDays = parseInt(cutoffDays, 10);
                if (!/^\d+$/.test(cutoffDays) ||
                    isNaN(parsedCutoffDays) ||
                    parsedCutoffDays < 1 ||
                    parsedCutoffDays > 365) {
                    alert("Enter Booking Cutoff days from 1 to 365.");
                    return;
                }
                value = String(parsedCutoffDays);
            }
        } else {
            value = ((document.getElementById("bulkRestrictionNumber") || {}).value || "").trim();
            if (!/^\d+$/.test(value)) {
                alert("Enter a valid whole number.");
                return;
            }
            var numberValue = parseInt(value, 10);
            if (type !== "max_stay" && numberValue < 1) {
                alert("Minimum stay must be at least 1 night.");
                return;
            }
        }

        var days = Array.from(document.querySelectorAll(".bulkDay:checked"))
            .map(function (x) { return parseInt(x.value, 10); });
        if (days.length === 0) { alert("Select at least one day."); return; }

        var payload = {
            hotelId: "<%= _hotelId %>",
            categoryId: categoryId,
            planId: planId,
            start: start,
            end: end,
            days: days,
            restriction: type,
            value: value
        };

        await BULK_postSave("SaveBulkRestriction", { req: payload });
    }

    function BULK_findCalendarCell(
        rowType,
        categoryId,
        planId,
        dateValue) {
        var cells = document.querySelectorAll(
            'td[data-rowtype="' + rowType + '"]');

        for (var index = 0; index < cells.length; index++) {
            var cell = cells[index];

            if ((cell.getAttribute("data-category") || "") === categoryId &&
                (cell.getAttribute("data-plan") || "") === planId &&
                (cell.getAttribute("data-date") || "") === dateValue) {
                return cell;
            }
        }

        return null;
    }

    function BULK_applyBookingCutoffSaveResult(data) {
        if (!data ||
            data.restriction !== "booking_cutoff" ||
            !Array.isArray(data.cells)) {
            return false;
        }

        var updatedCells = 0;

        data.cells.forEach(function (item) {
            var cutoffCell = BULK_findCalendarCell(
                "booking_cutoff",
                data.categoryId,
                data.planId,
                item.date);

            if (cutoffCell) {
                cutoffCell.setAttribute(
                    "data-current",
                    item.rawValue || "");

                cutoffCell.setAttribute(
                    "data-cutoff-mode",
                    item.cutoffMode || "");

                cutoffCell.setAttribute(
                    "data-cutoff-days",
                    item.cutoffDays || "");

                var badge = cutoffCell.querySelector(
                    ".restriction-grid-value");

                if (badge) {
                    badge.classList.remove(
                        "restriction-cutoff-open",
                        "restriction-cutoff-closed",
                        "restriction-cutoff-disabled");

                    badge.classList.add(
                        item.valueClass ||
                        "restriction-cutoff-disabled");

                    badge.setAttribute(
                        "title",
                        item.tooltip || "");

                    var modeText = badge.querySelector(
                        ".cutoff-mode-text");

                    if (modeText) {
                        modeText.textContent =
                            item.modeText || "";
                    }

                    var stateDot = badge.querySelector(
                        ".cutoff-state-dot");

                    if (stateDot) {
                        stateDot.classList.remove(
                            "cutoff-state-open",
                            "cutoff-state-closed");

                        stateDot.classList.add(
                            item.stateClass ||
                            "cutoff-state-open");
                    }

                    var stateText = badge.querySelector(
                        ".cutoff-state-text");

                    if (stateText) {
                        stateText.textContent =
                            item.statusText || "";
                    }
                }

                updatedCells++;
            }

            var rateCell = BULK_findCalendarCell(
                "rate",
                data.categoryId,
                data.planId,
                item.date);

            if (rateCell) {
                var blocked =
                    item.effectiveStopSell === true;

                rateCell.classList.toggle(
                    "effective-stop-sell",
                    blocked);

                rateCell.setAttribute(
                    "data-effective-stop-sell",
                    blocked ? "1" : "0");

                var blockReason = blocked
                    ? item.manualStopSell === true
                        ? "Manual Stop Sell"
                        : "Booking Cutoff"
                    : "";

                rateCell.setAttribute(
                    "data-block-reason",
                    blockReason);

                rateCell.setAttribute(
                    "title",
                    blocked
                        ? "Blocked by " +
                        blockReason +
                        "."
                        : "");
            }
        });

        return updatedCells > 0;
    }

    async function BULK_postSave(method, body) {
        var save = document.getElementById("bulkSaveButton");
        var originalText = save ? save.textContent : "Save";
        var reopenGroup = method === "SaveBulkRestriction" && BULK_catId && BULK_planId
            ? (BULK_catId + "||" + BULK_planId)
            : "";

        if (save) { save.disabled = true; save.textContent = "Saving..."; }

        try {
            var url = window.location.pathname.replace(/\/+$/, "") + "/" + method;
            var response = await fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json; charset=utf-8" },
                body: JSON.stringify(body)
            });

            var raw = await response.text();
            if (raw.trim().charAt(0) === "<") throw new Error("Save returned HTML. Check login/session or server error.");

            var json = JSON.parse(raw);
            var data = json.d;
            if (!data || data.ok !== true) throw new Error((data && (data.message || data.error)) || "Save failed");

            if (reopenGroup) {
                BULK_rememberRestrictionGroup(
                    reopenGroup,
                    true);
            }

            var appliedInstantly =
                method === "SaveBulkRestriction" &&
                BULK_applyBookingCutoffSaveResult(data);

            BULK_closeModalSafe();
            BULK_fullClear();

            /*
             * Booking Cutoff returns the committed database state and
             * updates only the affected cells. Other save types keep the
             * existing server reload behaviour.
             */
            if (!appliedInstantly) {
                var btnReload = document.getElementById(
                    "<%= btnBulkReload.ClientID %>");

                if (btnReload) {
                    btnReload.click();
                } else {
                    __doPostBack(
                        "<%= mainRepeater.UniqueID %>",
                        "");
                }
            }
        } catch (error) {
            alert("Save error: " + error.message);
        } finally {
            if (save) { save.disabled = false; save.textContent = originalText; }
        }
    }

    function bulkSaveSelected() {
        var type = (document.getElementById("bulkUpdateType") || {}).value || "rate";
        if (type === "rate") return bulkSaveRates();
        return BULK_saveRestriction();
    }

    function BULK_bindModalCloseClear() {
        var modal = document.getElementById("bulkRateModal");
        if (!modal || modal.__bulkCloseBound === true) return;
        modal.__bulkCloseBound = true;

        modal.addEventListener("hidden.bs.modal", BULK_fullClear);
        modal.addEventListener("click", function (e) {
            var t = e.target;
            if (t && t.matches("[data-bs-dismiss='modal'], .btn-close, .bulkClose")) {
                setTimeout(BULK_fullClear, 0);
            }
        }, true);
    }

    window.BULK_onUpdateTypeChanged = BULK_onUpdateTypeChanged;
    window.BULK_onCutoffModeChanged = BULK_onCutoffModeChanged;
    window.bulkPreviewRatesDebounced = BULK_previewRatesDebounced;
    window.bulkSaveRates = bulkSaveRates;
    window.bulkSaveSelected = bulkSaveSelected;

    document.addEventListener("DOMContentLoaded", function () {
        BULK_bindDrag();
        BULK_bindModalCloseClear();
        BULK_onUpdateTypeChanged();
        BULK_restoreRestrictionGroups();
    });

    if (window.Sys && Sys.WebForms && Sys.WebForms.PageRequestManager) {
        Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
            BULK_isDragging = false;
            BULK_startCell = null;
            BULK_didDragMove = false;
            BULK_clearSelection();
            BULK_bindDrag();
            BULK_bindModalCloseClear();
            BULK_restoreRestrictionGroups();
        });
    }
</script>
            <script>
                function findCell(el) {
                    while (el && el !== document) {
                        if (el.classList && el.classList.contains("calCell")) return el;
                        el = el.parentNode;
                    }
                    return null;
                }

                function isValidRateText(value) {
                    var text = (value || "").trim();

                    // Required, positive number, maximum 2 decimal places.
                    if (text === "") return false;
                    if (!/^\d+(?:\.\d{1,2})?$/.test(text)) return false;

                    var amount = Number(text);
                    return Number.isFinite(amount) && amount > 0;
                }

                function sanitizeRateText(value) {
                    var text = (value || "").replace(/[^0-9.]/g, "");
                    var firstDot = text.indexOf(".");

                    if (firstDot >= 0) {
                        text = text.substring(0, firstDot + 1) +
                            text.substring(firstDot + 1).replace(/\./g, "");

                        var parts = text.split(".");
                        text = parts[0] + "." + (parts[1] || "").substring(0, 2);
                    }

                    return text;
                }

                function showRateValidation(tb, message) {
                    tb.classList.add("invalid-rate");
                    tb.setAttribute("aria-invalid", "true");
                    tb.setAttribute("title", message || "Invalid rate");
                    setTimeout(function () {
                        tb.focus();
                        try { tb.select(); } catch (e) { }
                    }, 0);
                }

                function lockTB(tb, restoreOriginal) {
                    if (restoreOriginal === true && tb.dataset.originalRate !== undefined) {
                        tb.value = tb.dataset.originalRate;
                    }

                    tb.readOnly = true;
                    tb.classList.remove(
                        "editing-rate",
                        "rate-dirty",
                        "saving-rate",
                        "invalid-rate");
                    tb.removeAttribute("aria-invalid");
                    tb.removeAttribute("title");

                    tb.style.pointerEvents = "none";

                    var cell = findCell(tb);
                    if (cell) {
                        cell.classList.remove("individual-rate-open");
                    }

                    updateSaveRatesButtonState();
                }

                function unlockTB(tb, focusInput) {
                    var cell = findCell(tb);

                    if (!cell ||
                        (cell.getAttribute("data-rowtype") || "") !== "rate" ||
                        !canUpdateRate(cell) ||
                        (cell.getAttribute("data-derivedlocked") || "0") === "1") {
                        return;
                    }

                    // Do not overwrite the original value if this day is already open.
                    if (!tb.classList.contains("editing-rate")) {
                        tb.dataset.originalRate = (tb.value || "").trim();
                    }

                    tb.readOnly = false;
                    tb.classList.remove("invalid-rate");
                    tb.classList.add("editing-rate");
                    tb.style.pointerEvents = "auto";
                    tb.setAttribute("inputmode", "decimal");
                    tb.setAttribute("autocomplete", "off");
                    cell.classList.add("individual-rate-open");

                    markRateDirty(tb);
                    updateSaveRatesButtonState();

                    if (focusInput !== false) {
                        setTimeout(function () {
                            tb.focus();
                            try { tb.select(); } catch (e) { }
                        }, 0);
                    }
                }

                function isSameRateValue(a, b) {
                    var aa = (a == null ? "" : String(a)).trim();
                    var bb = (b == null ? "" : String(b)).trim();

                    if (isValidRateText(aa) && isValidRateText(bb)) {
                        return Number(aa) === Number(bb);
                    }

                    return aa === bb;
                }

                function markRateDirty(tb) {
                    if (!tb || !tb.classList.contains("editing-rate")) return false;

                    var original =
                        tb.dataset.originalRate !== undefined
                            ? tb.dataset.originalRate
                            : "";

                    var dirty = !isSameRateValue(tb.value, original);
                    tb.classList.toggle("rate-dirty", dirty);

                    var cell = findCell(tb);
                    if (cell) {
                        cell.classList.toggle("rate-dirty-cell", dirty);
                    }

                    return dirty;
                }

                function updateSaveRatesButtonState() {
                    var btn = document.getElementById("btnSaveEditedRates");
                    if (!btn) return;

                    var root =
                        document.getElementById("<%= maindiv.ClientID %>") ||
                        document;

                    var hasRatePermission =
                        root.querySelector(
                            ".calCell[data-rowtype='rate'][data-canupdaterate='1']"
                        ) !== null;

                    // Permission-wise display: users without RateUpdate never see the
                    // manual daily-price Save action.
                    btn.style.display = hasRatePermission ? "" : "none";

                    if (!hasRatePermission) {
                        btn.disabled = true;
                        btn.classList.remove("has-rate-changes");
                        return;
                    }

                    var dirtyCount =
                        root.querySelectorAll(
                            ".calCell[data-rowtype='rate'] .calInput.editing-rate.rate-dirty"
                        ).length;

                    btn.disabled = dirtyCount === 0;
                    btn.classList.toggle("has-rate-changes", dirtyCount > 0);
                    btn.setAttribute(
                        "title",
                        dirtyCount > 0
                            ? "Save " + dirtyCount + " changed price" + (dirtyCount === 1 ? "" : "s")
                            : "No price changes to save");
                }

                function applyUpdatedSingleDayRates(
                    categoryId,
                    date,
                    updatedRates) {
                    if (!Array.isArray(updatedRates) ||
                        updatedRates.length === 0) {
                        return false;
                    }

                    var rateByPlan = Object.create(null);

                    updatedRates.forEach(function (item) {
                        if (!item) return;

                        var planId = String(item.planId || "").trim();
                        var rate = String(item.rate || "").trim();

                        if (planId && rate) {
                            rateByPlan[planId] = rate;
                        }
                    });

                    var changed = false;
                    var root =
                        document.getElementById(
                            "<%= maindiv.ClientID %>") ||
                        document;

                    var cells = root.querySelectorAll(
                        ".calCell[data-rowtype='rate']");

                    cells.forEach(function (rateCell) {
                        if ((rateCell.getAttribute("data-category") || "") !== categoryId) {
                            return;
                        }

                        if ((rateCell.getAttribute("data-date") || "") !== date) {
                            return;
                        }

                        var planId = rateCell.getAttribute("data-plan") || "";

                        if (!Object.prototype.hasOwnProperty.call(
                            rateByPlan,
                            planId)) {
                            return;
                        }

                        var input = rateCell.querySelector(
                            "input[type='text'][id$='txtrate']");

                        if (!input) return;

                        // Never overwrite another unsaved individual edit.
                        if (input.classList.contains("rate-dirty")) {
                            return;
                        }

                        input.value = rateByPlan[planId];
                        input.dataset.originalRate = rateByPlan[planId];

                        input.classList.remove(
                            "editing-rate",
                            "rate-dirty",
                            "saving-rate",
                            "invalid-rate");

                        input.readOnly = true;
                        input.style.pointerEvents = "none";

                        rateCell.classList.remove(
                            "individual-rate-open",
                            "rate-dirty-cell");
                        rateCell.classList.add("saved-rate");

                        setTimeout(function () {
                            rateCell.classList.remove("saved-rate");
                        }, 1200);

                        changed = true;
                    });

                    return changed;
                }

                function captureAvailabilityScrollState() {
                    var root =
                        document.getElementById("<%= maindiv.ClientID %>");

                    window.__availabilityScrollState = root
                        ? {
                            left: root.scrollLeft || 0,
                            top: root.scrollTop || 0
                        }
                        : null;
                }

                function restoreAvailabilityScrollState() {
                    var state =
                        window.__availabilityScrollState;

                    if (!state) {
                        return;
                    }

                    var root =
                        document.getElementById("<%= maindiv.ClientID %>");

                    if (root) {
                        root.scrollLeft =
                            Number(state.left || 0);

                        root.scrollTop =
                            Number(state.top || 0);
                    }

                    window.__availabilityScrollState = null;
                }

                function refreshAvailabilityRepeater() {
                    captureAvailabilityScrollState();

                    var reloadButton =
                        document.getElementById(
                            "<%= btnBulkReload.ClientID %>");

                    if (!reloadButton) {
                        return false;
                    }

                    var prm = null;

                    if (window.Sys &&
                        Sys.WebForms &&
                        Sys.WebForms.PageRequestManager) {
                        prm =
                            Sys.WebForms.PageRequestManager
                                .getInstance();
                    }

                    if (prm &&
                        prm.get_isInAsyncPostBack()) {
                        window.setTimeout(
                            refreshAvailabilityRepeater,
                            100);
                        return true;
                    }

                    reloadButton.click();
                    return true;
                }

                function validateRateForSave(tb) {
                    var cell = findCell(tb);
                    if (!cell ||
                        (cell.getAttribute("data-rowtype") || "") !== "rate" ||
                        !canUpdateRate(cell)) {
                        return {
                            ok: false,
                            message: "You do not have permission to update this rate."
                        };
                    }

                    var date = cell.getAttribute("data-date") || "";
                    var planId = cell.getAttribute("data-plan") || "";
                    var categoryId = cell.getAttribute("data-category") || "";
                    var rateText = (tb.value || "").trim();

                    if (!date || !planId || !categoryId) {
                        return { ok: false, message: "Missing rate cell information." };
                    }

                    if (rateText === "") {
                        return { ok: false, message: "Rate is required and cannot be empty." };
                    }

                    if (!isValidRateText(rateText)) {
                        return {
                            ok: false,
                            message: "Enter a valid rate using numbers only, with up to 2 decimal places."
                        };
                    }

                    var rate = Number(rateText);
                    var baseRateField = document.getElementById("hfHotelBaseRate");
                    var baseRate =
                        Number((baseRateField && baseRateField.value) || "0");

                    if (Number.isFinite(baseRate) &&
                        baseRate > 0 &&
                        rate < baseRate) {
                        return {
                            ok: false,
                            code: "below_base_rate",
                            baseRate: baseRate,
                            rate: rate,
                            message:
                                "Rate cannot be less than the base rate " +
                                baseRate.toFixed(2) +
                                "."
                        };
                    }

                    return {
                        ok: true,
                        payload: {
                            hotelId: "<%= _hotelId %>",
                            categoryId: categoryId,
                            planId: planId,
                            date: date,
                            rate: rate.toFixed(2)
                        }
                    };
                }

                function saveRateWithoutGridRefresh(tb) {
                    var validation = validateRateForSave(tb);

                    if (!validation.ok) {
                        showRateValidation(tb, validation.message);
                        return Promise.resolve({
                            ok: false,
                            message: validation.message
                        });
                    }

                    tb.classList.add("saving-rate");

                    return new Promise(function (resolve) {
                        if (!window.PageMethods || !PageMethods.SaveSingleRate) {
                            tb.classList.remove("saving-rate");
                            resolve({
                                ok: false,
                                message:
                                    "PageMethods not available. Add ScriptManager EnablePageMethods='true'."
                            });
                            return;
                        }

                        PageMethods.SaveSingleRate(
                            validation.payload,
                            function (res) {
                                tb.classList.remove("saving-rate");

                                if (!res || res.ok !== true) {
                                    resolve({
                                        ok: false,
                                        message:
                                            (res && (res.message || res.error))
                                                ? (res.message || res.error)
                                                : "Save failed"
                                    });
                                    return;
                                }

                                // Do not update derived descendants here because another
                                // open cell may still contain an unsaved, different price.
                                // The full grid is refreshed once after all edits are saved.
                                tb.value = validation.payload.rate;
                                tb.dataset.originalRate = validation.payload.rate;
                                tb.classList.remove(
                                    "rate-dirty",
                                    "invalid-rate");

                                var cell = findCell(tb);
                                if (cell) {
                                    cell.classList.remove("rate-dirty-cell");
                                    cell.classList.add("saved-rate");
                                }

                                resolve({
                                    ok: true,
                                    response: res
                                });
                            },
                            function (err) {
                                tb.classList.remove("saving-rate");
                                console.log(err);
                                resolve({
                                    ok: false,
                                    message: "Save error."
                                });
                            });
                    });
                }

                async function saveSingleRate(tb) {
                    var result = await saveRateWithoutGridRefresh(tb);

                    if (!result.ok) {
                        console.warn(result.message || "Save failed.");
                        return false;
                    }

                    lockTB(tb, false);

                    if (!refreshAvailabilityRepeater()) {
                        console.warn(
                            "The rate was saved, but the availability grid could not refresh automatically. Please reload the page.");
                    }

                    return true;
                }

                async function saveAllEditedRates() {
                    var root =
                        document.getElementById("<%= maindiv.ClientID %>") ||
                        document;

                    var dirtyInputs = Array.from(
                        root.querySelectorAll(
                            ".calCell[data-rowtype='rate'] .calInput.editing-rate.rate-dirty"
                        ));

                    if (dirtyInputs.length === 0) {
                        updateSaveRatesButtonState();
                        return;
                    }

                    // Validate each changed date independently.
                    // A rate below the hotel base rate is skipped, while the
                    // other valid dates are still saved. Other validation
                    // errors remain blocking because they indicate malformed
                    // or incomplete cell data.
                    var validInputs = [];
                    var skippedBelowBase = [];

                    for (var i = 0; i < dirtyInputs.length; i++) {
                        var validation = validateRateForSave(dirtyInputs[i]);

                        if (validation.ok) {
                            validInputs.push(dirtyInputs[i]);
                            continue;
                        }

                        if (validation.code === "below_base_rate") {
                            skippedBelowBase.push({
                                input: dirtyInputs[i],
                                validation: validation
                            });
                            showRateValidation(
                                dirtyInputs[i],
                                validation.message);
                            continue;
                        }

                        showRateValidation(
                            dirtyInputs[i],
                            validation.message);
                        return;
                    }

                    // Nothing is valid to save. Keep the invalid cells visible
                    // so the user can correct them without losing the edits.
                    if (validInputs.length === 0) {
                        if (skippedBelowBase.length > 0) {
                            console.warn(
                                skippedBelowBase.length === 1
                                    ? "1 rate was not saved because it is below the base rate."
                                    : skippedBelowBase.length +
                                      " rates were not saved because they are below the base rate.");
                        }
                        updateSaveRatesButtonState();
                        return;
                    }

                    var btn = document.getElementById("btnSaveEditedRates");
                    var originalButtonText =
                        btn ? btn.innerHTML : "💾 Save";

                    if (btn) {
                        btn.disabled = true;
                        btn.innerHTML = "Saving...";
                    }

                    var savedCount = 0;

                    try {
                        // Save only valid dates sequentially so derived-rate
                        // calculations stay deterministic. Dates below the base
                        // rate are deliberately left unsaved.
                        for (var index = 0; index < validInputs.length; index++) {
                            var tb = validInputs[index];
                            var result =
                                await saveRateWithoutGridRefresh(tb);

                            if (!result.ok) {
                                var saveMessage =
                                    result.message ||
                                    "The next rate could not be saved.";

                                // Keep saving the remaining dates when the
                                // server rejects only this date for being below
                                // the base rate. This also protects against a
                                // stale client-side base-rate value.
                                if (saveMessage
                                    .toLowerCase()
                                    .indexOf("less than the base rate") >= 0) {
                                    showRateValidation(tb, saveMessage);
                                    skippedBelowBase.push({
                                        input: tb,
                                        validation: {
                                            code: "below_base_rate",
                                            message: saveMessage
                                        }
                                    });
                                    continue;
                                }

                                console.warn(
                                    "Saved " + savedCount + " of " +
                                    validInputs.length +
                                    " valid price changes. " +
                                    saveMessage);

                                refreshAvailabilityRepeater();
                                return;
                            }

                            savedCount++;
                        }

                        // Lock only the successfully saved cells. Below-base
                        // cells remain dirty/open until the refresh restores the
                        // persisted value from the database.
                        validInputs.forEach(function (tb) {
                            lockTB(tb, false);
                        });

                        if (!refreshAvailabilityRepeater()) {
                            console.warn(
                                "Prices were saved, but the inventory grid could not refresh automatically. Please reload the page.");
                            return;
                        }

                        // No browser alert on Save. The grid refresh itself is the
                        // confirmation, while below-base cells remain visibly invalid.
                    } finally {
                        if (btn) {
                            btn.innerHTML = originalButtonText;
                        }

                        updateSaveRatesButtonState();
                    }
                }

                function focusNextOpenRate(tb) {
                    var row = tb && tb.closest ? tb.closest("tr") : null;
                    if (!row) return;

                    var inputs = Array.from(
                        row.querySelectorAll(
                            "input[type='text'][id$='txtrate'].editing-rate"
                        ));

                    var index = inputs.indexOf(tb);
                    if (index >= 0 && index + 1 < inputs.length) {
                        inputs[index + 1].focus();
                        try { inputs[index + 1].select(); } catch (ignore) { }
                    }
                }

                function bindRateEditing() {
                    var root =
                        document.getElementById("<%= maindiv.ClientID %>") ||
                        document;

                    if (root.__rateEditBound) return;
                    root.__rateEditBound = true;

                    // Single click opens one rate cell for manual editing.
                    // A real drag remains reserved for the Bulk Rate Update popup.
                    root.addEventListener("click", function (e) {
                        var cell = findCell(e.target);
                        if (!cell) return;
                        if ((cell.getAttribute("data-rowtype") || "") !== "rate") return;

                        if (window.__suppressRateCellClickUntil &&
                            Date.now() < window.__suppressRateCellClickUntil) {
                            e.preventDefault();
                            e.stopPropagation();
                            return;
                        }

                        if (!canUpdateRate(cell)) {
                            e.preventDefault();
                            e.stopPropagation();
                            return;
                        }

                        var tb = cell.querySelector(
                            "input[type='text'][id$='txtrate']");
                        if (!tb) return;

                        unlockTB(tb, true);

                        e.preventDefault();
                        e.stopPropagation();
                    }, true);

                    // When the user moves to the next rate textbox with TAB, make that
                    // textbox part of the same manual-edit workflow immediately. Without
                    // this, a textbox reached only by keyboard could accept a value change
                    // without receiving the editing-rate class, so its changed-cell border
                    // was not applied.
                    root.addEventListener("focusin", function (e) {
                        var t = e.target;

                        if (!(t &&
                            t.tagName === "INPUT" &&
                            (t.id || "").endsWith("txtrate"))) {
                            return;
                        }

                        var cell = findCell(t);
                        if (!cell ||
                            (cell.getAttribute("data-rowtype") || "") !== "rate" ||
                            !canUpdateRate(cell) ||
                            (cell.getAttribute("data-derivedlocked") || "0") === "1") {
                            return;
                        }

                        // focusInput=false prevents re-focusing/selecting and keeps normal
                        // browser TAB navigation intact.
                        unlockTB(t, false);
                    }, true);

                    // Keep the open rate cells numeric and mark only changed days as dirty.
                    root.addEventListener("input", function (e) {
                        var t = e.target;

                        if (!(t &&
                            t.tagName === "INPUT" &&
                            (t.id || "").endsWith("txtrate"))) {
                            return;
                        }

                        if (!t.classList.contains("editing-rate")) return;

                        var cleanValue = sanitizeRateText(t.value);
                        if (t.value !== cleanValue) {
                            t.value = cleanValue;
                        }

                        t.classList.remove("invalid-rate");
                        t.removeAttribute("aria-invalid");
                        t.removeAttribute("title");
                        markRateDirty(t);
                        updateSaveRatesButtonState();
                    }, true);

                    /*
                     * Point 2:
                     * Enter no longer saves. It simply moves to the next open date.
                     * Tab keeps its normal browser behaviour.
                     * Save is performed only by the Save button beside Auto Update.
                     */
                    root.addEventListener("keydown", function (e) {
                        var t = e.target;
                        if (!t) return;

                        if (!(t.tagName === "INPUT" &&
                            (t.id || "").endsWith("txtrate"))) {
                            return;
                        }

                        if (e.key === "Enter") {
                            e.preventDefault();
                            markRateDirty(t);
                            updateSaveRatesButtonState();
                            focusNextOpenRate(t);
                            return;
                        }

                        if (e.key === "Tab") {
                            // Re-evaluate the current value before focus leaves the cell.
                            // The normal TAB action is intentionally not prevented.
                            var cleanValue = sanitizeRateText(t.value);
                            if (t.value !== cleanValue) {
                                t.value = cleanValue;
                            }
                            t.classList.remove("invalid-rate");
                            markRateDirty(t);
                            updateSaveRatesButtonState();
                            return;
                        }

                        if (e.key === "Escape") {
                            e.preventDefault();
                            lockTB(t, true);
                        }
                    }, true);

                    // Do not cancel on blur. Users may move across the open dates,
                    // set a different price for each day, then press Save once.
                    root.addEventListener("blur", function (e) {
                        var t = e.target;

                        if (!(t &&
                            t.tagName === "INPUT" &&
                            (t.id || "").endsWith("txtrate"))) {
                            return;
                        }

                        if (!t.classList.contains("editing-rate")) return;

                        markRateDirty(t);
                        updateSaveRatesButtonState();
                    }, true);

                    updateSaveRatesButtonState();
                }

                window.saveAllEditedRates = saveAllEditedRates;
                document.addEventListener("DOMContentLoaded", bindRateEditing);

                // UpdatePanel support
                if (window.Sys && Sys.WebForms && Sys.WebForms.PageRequestManager) {
                    Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
                        var root =
                            document.getElementById(
                                "<%= maindiv.ClientID %>") ||
                            document;

                        root.__rateEditBound = false;
                        bindRateEditing();

                        window.setTimeout(
                            restoreAvailabilityScrollState,
                            0);
                    });
                }
            </script>

<style>
  .calCell .calInput { pointer-events:none; } /* normal */
  .calCell .calInput.editing-rate {
      pointer-events:auto;
      border:1px solid #cbd5e1;
      background:#fff !important;
  }
  .calCell.individual-rate-open {
      box-shadow:inset 0 0 0 1px #8ab6cf;
  }
  .calCell.rate-dirty-cell {
      border:2px solid #dc2626 !important;
      box-shadow:inset 0 0 0 1px #dc2626;
      background:#ffdede !important;
  }
  .calCell.rate-dirty-cell .calInput,
  .calCell .calInput.rate-dirty {
      font-weight:700;
      background:#ffdede !important;
      color:#7f1d1d !important;
  }
  .calCell .calInput.saving-rate { opacity:.6; }
  .calCell .calInput.invalid-rate {
      border:1px solid #dc2626 !important;
      box-shadow:0 0 0 2px rgba(220,38,38,.14);
      background:#fff7f7 !important;
  }
  .calCell.saved-rate { outline:2px solid #16a34a; }

  .plan-name-label {
      white-space:nowrap;
      font-weight:normal;
  }

  /*
   * Keep the rate-plan name and all inventory cells visually unchanged.
   * Show only a compact lock beside a derived plan when editing is disabled.
   */
  .derived-rate-lock {
      display:inline-block;
      margin-left:4px;
      padding:0;
      border:0;
      background:transparent;
      color:inherit;
      font-size:10px;
      line-height:1;
      vertical-align:middle;
      cursor:help;
  }
</style>
<!-- =============================================================
     Availability Change Log - right-side timeline
     ============================================================= -->
<div id="availabilityLogModal"
     class="availability-log-overlay"
     aria-hidden="true">
    <div class="availability-log-drawer"
         role="dialog"
         aria-modal="true"
         aria-labelledby="availabilityLogTitle">
        <div class="availability-log-header">
            <div class="availability-log-header-copy">
                <div class="availability-log-eyebrow" style="display:none;">CHANNEL MANAGER</div>
                <div class="availability-log-title" id="availabilityLogTitle">Availability Change Log</div>
                <div class="availability-log-subtitle" id="availabilityLogSubtitle">Select an availability cell</div>
            </div>
            <button type="button"
                    class="availability-log-close"
                    id="availabilityLogClose"
                    aria-label="Close">×</button>
        </div>

        <div class="availability-log-body">
            <div class="availability-log-summary">
                <div class="availability-log-summary-block">
                    <span class="availability-log-summary-label">Selected date</span>
                    <strong id="availabilityLogDate">—</strong>
                </div>
                <div class="availability-log-summary-block availability-log-category-block">
                    <span class="availability-log-summary-label">Category</span>
                    <strong id="availabilityLogCategory">—</strong>
                </div>
                <div class="availability-log-summary-count">
                    <span id="availabilityLogCount">—</span>
                    <small>Changes</small>
                </div>
            </div>

            <div class="availability-log-order">
                <span class="availability-log-order-dot"></span>
                Latest changes are shown first
            </div>

            <div id="availabilityLogLoading" class="availability-log-state">
                <div class="availability-log-spinner"></div>
                <span>Loading availability changes...</span>
            </div>

            <div id="availabilityLogError"
                 class="availability-log-state availability-log-error"
                 style="display:none;"></div>

            <div id="availabilityLogEmpty"
                 class="availability-log-state availability-log-empty"
                 style="display:none;">
                No availability change was found for this date.
            </div>

            <div id="availabilityLogTimeline"
                 class="availability-log-timeline"
                 style="display:none;"></div>
        </div>
    </div>
</div>

<style>
    /* =========================================================
       Availability history drawer - right side timeline
       ========================================================= */
    .availability-log-overlay {
        position: fixed;
        inset: 0;
        z-index: 10050;
        display: block;
        visibility: hidden;
        pointer-events: none;
        background: rgba(15, 23, 42, .26);
        opacity: 0;
        transition: opacity .18s ease, visibility .18s ease;
    }

    .availability-log-overlay.is-open {
        visibility: visible;
        pointer-events: auto;
        opacity: 1;
    }

    .availability-log-drawer {
        position: absolute;
        top: 0;
        right: 0;
        width: 500px;
        max-width: calc(100vw - 18px);
        height: 100vh;
        display: flex;
        flex-direction: column;
        background: #f6f8fa;
        border-left: 1px solid #dce3e9;
        box-shadow: -18px 0 48px rgba(15, 23, 42, .20);
        transform: translateX(100%);
        transition: transform .22s ease;
    }

    .availability-log-overlay.is-open .availability-log-drawer {
        transform: translateX(0);
    }

    .availability-log-header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 16px;
        padding: 18px 18px 16px;
        background: #17283b;
        border-bottom: 3px solid #20b996;
        color: #fff;
    }

    .availability-log-header-copy {
        min-width: 0;
    }

    .availability-log-eyebrow {
        margin-bottom: 5px;
        color: #83e0c8;
        font-size: 9px;
        font-weight: 800;
        letter-spacing: 1.6px;
        line-height: 1.2;
    }

    .availability-log-title {
        color: #fff;
        font-size: 18px;
        font-weight: 650;
        line-height: 1.25;
    }

    .availability-log-subtitle {
        margin-top: 5px;
        color: rgba(255,255,255,.72);
        font-size: 11px;
        line-height: 1.4;
    }

    .availability-log-close {
        width: 32px;
        height: 32px;
        flex: 0 0 32px;
        padding: 0;
        border: 1px solid rgba(255,255,255,.20);
        border-radius: 5px;
        background: rgba(255,255,255,.08);
        color: #fff;
        font-size: 20px;
        line-height: 28px;
        text-align: center;
        cursor: pointer;
    }

    .availability-log-close:hover {
        background: rgba(255,255,255,.17);
    }

    .availability-log-body {
        min-height: 0;
        flex: 1 1 auto;
        overflow-y: auto;
        padding: 14px 16px 24px;
    }

    .availability-log-summary {
        display: grid;
        grid-template-columns: 112px minmax(0, 1fr) 76px;
        gap: 0;
        margin-bottom: 9px;
        overflow: hidden;
        border: 1px solid #dfe5ea;
        border-radius: 7px;
        background: #fff;
    }

    .availability-log-summary-block {
        min-width: 0;
        padding: 10px 11px;
        border-right: 1px solid #e6ebef;
    }

    .availability-log-summary-label {
        display: block;
        margin-bottom: 4px;
        color: #7b8794;
        font-size: 8px;
        font-weight: 800;
        letter-spacing: .65px;
        line-height: 1.2;
        text-transform: uppercase;
    }

    .availability-log-summary-block strong {
        display: block;
        overflow: hidden;
        color: #26384a;
        font-size: 11px;
        font-weight: 650;
        line-height: 1.35;
        text-overflow: ellipsis;
        white-space: nowrap;
    }

    .availability-log-summary-count {
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        padding: 8px 6px;
        background: #f8fbfa;
    }

    .availability-log-summary-count span {
        color: #13785f;
        font-size: 20px;
        font-weight: 750;
        line-height: 1;
    }

    .availability-log-summary-count small {
        margin-top: 4px;
        color: #7b8794;
        font-size: 8px;
        font-weight: 700;
        letter-spacing: .45px;
        text-transform: uppercase;
    }

    .availability-log-order {
        display: flex;
        align-items: center;
        gap: 6px;
        margin: 0 2px 12px;
        color: #7a8794;
        font-size: 9px;
        line-height: 1.3;
    }

    .availability-log-order-dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: #20b996;
    }

    .availability-log-timeline {
        position: relative;
        padding: 2px 0 0;
    }

    .availability-log-timeline::before {
        content: "";
        position: absolute;
        top: 12px;
        bottom: 16px;
        left: 14px;
        width: 2px;
        background: #dce5e9;
    }

    .availability-log-item {
        position: relative;
        display: grid;
        grid-template-columns: 30px minmax(0, 1fr);
        gap: 9px;
        padding: 0 0 13px;
    }

    .availability-log-node {
        position: relative;
        z-index: 1;
        width: 12px;
        height: 12px;
        margin: 11px 0 0 9px;
        border: 3px solid #fff;
        border-radius: 50%;
        box-shadow: 0 0 0 1px #b9c7cf;
        background: #20b996;
    }

    .availability-log-node.is-failed {
        background: #d92d20;
    }

    .availability-log-card {
        overflow: hidden;
        border: 1px solid #dfe5ea;
        border-radius: 7px;
        background: #fff;
        box-shadow: 0 2px 7px rgba(15, 23, 42, .045);
    }

    .availability-log-card-top {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        padding: 9px 11px 7px;
        border-bottom: 1px solid #edf1f4;
    }

    .availability-log-time {
        color: #536477;
        font-size: 10px;
        font-weight: 650;
        line-height: 1.3;
    }

    .availability-log-badge {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 3px 7px;
        border: 1px solid transparent;
        border-radius: 999px;
        font-size: 8px;
        font-weight: 800;
        line-height: 1.25;
        white-space: nowrap;
    }

    .availability-log-badge::before {
        content: "";
        width: 5px;
        height: 5px;
        border-radius: 50%;
        background: currentColor;
    }

    .availability-log-success {
        color: #13805f;
        background: #ecf9f5;
        border-color: #cdebe2;
    }

    .availability-log-failed {
        color: #b42318;
        background: #fff1f0;
        border-color: #ffd5d2;
    }

    .availability-log-card-body {
        padding: 10px 11px 11px;
    }

    .availability-log-change-label {
        margin-bottom: 7px;
        color: #768697;
        font-size: 9px;
        font-weight: 700;
        letter-spacing: .25px;
        text-transform: uppercase;
    }

    .availability-log-change {
        display: flex;
        align-items: center;
        gap: 7px;
        margin-bottom: 9px;
    }

    .availability-log-value {
        display: inline-flex;
        min-width: 34px;
        height: 28px;
        align-items: center;
        justify-content: center;
        padding: 0 9px;
        border: 1px solid #d7e1e7;
        border-radius: 5px;
        background: #f6f8fa;
        color: #536477;
        font-size: 13px;
        font-weight: 750;
    }

    .availability-log-value.is-current {
        border-color: #c7e8df;
        background: #eaf8f4;
        color: #11765d;
    }

    .availability-log-arrow {
        color: #9aa8b5;
        font-size: 15px;
        line-height: 1;
    }

    .availability-log-set-text {
        display: flex;
        align-items: center;
        gap: 7px;
        margin-bottom: 9px;
        color: #536477;
        font-size: 10px;
    }

    .availability-log-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 5px 10px;
        color: #6f7f8e;
        font-size: 9px;
        line-height: 1.45;
    }

    .availability-log-meta strong {
        color: #425569;
        font-weight: 650;
    }

    .availability-log-meta-item {
        position: relative;
    }

    .availability-log-meta-item + .availability-log-meta-item::before {
        content: "•";
        margin-right: 8px;
        color: #b1bcc5;
    }

    .availability-log-system {
        margin-top: 5px;
        overflow-wrap: anywhere;
        color: #93a0ab;
        font-size: 8px;
        line-height: 1.4;
    }

    .availability-log-state {
        min-height: 180px;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 9px;
        padding: 24px;
        border: 1px dashed #d8e0e6;
        border-radius: 7px;
        background: #fff;
        color: #758493;
        font-size: 10px;
        text-align: center;
    }

    .availability-log-spinner {
        width: 23px;
        height: 23px;
        border: 3px solid #dfe7ec;
        border-top-color: #20b996;
        border-radius: 50%;
        animation: availabilityLogSpin .7s linear infinite;
    }

    .availability-log-error {
        color: #b42318;
        background: #fff7f6;
        border-color: #ffd5d2;
    }

    @keyframes availabilityLogSpin {
        to { transform: rotate(360deg); }
    }

    #Table4 .calCell[data-rowtype="availability"] {
        cursor: pointer;
    }

    body.availability-log-open {
        overflow: hidden;
    }

    @media (max-width: 600px) {
        .availability-log-drawer {
            width: calc(100vw - 10px);
            max-width: none;
        }

        .availability-log-summary {
            grid-template-columns: 105px minmax(0, 1fr) 68px;
        }

        .availability-log-category-block strong {
            white-space: normal;
        }
    }
</style>

<script type="text/javascript">
    (function () {
        function availabilityLogEscape(value) {
            return String(value == null ? "" : value)
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;")
                .replace(/"/g, "&quot;")
                .replace(/'/g, "&#39;");
        }

        function availabilityLogText(id, value) {
            var el = document.getElementById(id);
            if (el) el.textContent = value == null || value === "" ? "—" : value;
        }

        function availabilityLogDisplay(id, show) {
            var el = document.getElementById(id);
            if (el) el.style.display = show ? "" : "none";
        }

        function availabilityLogOpen() {
            var el = document.getElementById("availabilityLogModal");
            if (!el) return;

            el.classList.add("is-open");
            el.setAttribute("aria-hidden", "false");

            if (document.body) {
                document.body.classList.add("availability-log-open");
            }
        }

        function availabilityLogClose() {
            var el = document.getElementById("availabilityLogModal");
            if (!el) return;

            el.classList.remove("is-open");
            el.setAttribute("aria-hidden", "true");

            if (document.body) {
                document.body.classList.remove("availability-log-open");
            }
        }

        function availabilityLogSetLoading() {
            availabilityLogText("availabilityLogCount", "—");
            availabilityLogDisplay("availabilityLogLoading", true);
            availabilityLogDisplay("availabilityLogError", false);
            availabilityLogDisplay("availabilityLogEmpty", false);
            availabilityLogDisplay("availabilityLogTimeline", false);

            var timeline = document.getElementById("availabilityLogTimeline");
            if (timeline) timeline.innerHTML = "";
        }

        function availabilityLogRender(rows) {
            rows = Array.isArray(rows) ? rows : [];

            availabilityLogDisplay("availabilityLogLoading", false);
            availabilityLogText("availabilityLogCount", rows.length.toString());

            if (!rows.length) {
                availabilityLogDisplay("availabilityLogEmpty", true);
                availabilityLogDisplay("availabilityLogTimeline", false);
                return;
            }

            var html = "";

            rows.forEach(function (r) {
                var changedOn =
                    r.CreatedOn ||
                    (((r.LogDate || "") + " " + (r.LogTime || "")).trim()) ||
                    "—";

                var user = r.Username || r.UserId || "System";
                var success = !!r.IsSuccess;
                var statusClass = success
                    ? "availability-log-success"
                    : "availability-log-failed";
                var nodeClass = success
                    ? ""
                    : " is-failed";
                var statusText = success ? "Success" : "Failed";
                var currentValue = r.Availability == null || r.Availability === ""
                    ? "—"
                    : r.Availability;
                var previousValue = r.PreviousAvailability == null
                    ? ""
                    : String(r.PreviousAvailability).trim();

                var changeHtml;
                if (previousValue !== "") {
                    changeHtml =
                        "<div class='availability-log-change-label'>Availability changed</div>" +
                        "<div class='availability-log-change'>" +
                            "<span class='availability-log-value'>" +
                                availabilityLogEscape(previousValue) +
                            "</span>" +
                            "<span class='availability-log-arrow'>→</span>" +
                            "<span class='availability-log-value is-current'>" +
                                availabilityLogEscape(currentValue) +
                            "</span>" +
                        "</div>";
                } else {
                    changeHtml =
                        "<div class='availability-log-change-label'>Initial logged availability</div>" +
                        "<div class='availability-log-set-text'>" +
                            "<span>Set to</span>" +
                            "<span class='availability-log-value is-current'>" +
                                availabilityLogEscape(currentValue) +
                            "</span>" +
                        "</div>";
                }

                var systemInfo = [r.SystemName || "", r.IPAddress || ""]
                    .filter(function (x) { return !!x; })
                    .join(" · ");

                html +=
                    "<div class='availability-log-item'>" +
                        "<div class='availability-log-node" + nodeClass + "'></div>" +
                        "<div class='availability-log-card'>" +
                            "<div class='availability-log-card-top'>" +
                                "<div class='availability-log-time'>" +
                                    availabilityLogEscape(changedOn) +
                                "</div>" +
                                "<span class='availability-log-badge " + statusClass + "'>" +
                                    statusText +
                                "</span>" +
                            "</div>" +
                            "<div class='availability-log-card-body'>" +
                                changeHtml +
                                "<div class='availability-log-meta'>" +
                                    "<span class='availability-log-meta-item'>Changed by <strong>" +
                                        availabilityLogEscape(user) +
                                    "</strong></span>" +
                                    "<span class='availability-log-meta-item'>HTTP <strong>" +
                                        availabilityLogEscape(r.HttpStatus || "—") +
                                    "</strong></span>" +
                                "</div>" +
                                (systemInfo
                                    ? "<div class='availability-log-system'>" +
                                        availabilityLogEscape(systemInfo) +
                                      "</div>"
                                    : "") +
                            "</div>" +
                        "</div>" +
                    "</div>";
            });

            var timeline = document.getElementById("availabilityLogTimeline");
            if (timeline) timeline.innerHTML = html;

            availabilityLogDisplay("availabilityLogEmpty", false);
            availabilityLogDisplay("availabilityLogTimeline", true);
        }

        function openAvailabilityChangeLog(cell) {
            if (!cell) return;

            var rowType = (cell.getAttribute("data-rowtype") || "").toLowerCase();
            if (rowType !== "availability") return;

            var date = (cell.getAttribute("data-date") || "").trim();
            var categoryId = (cell.getAttribute("data-category") || "").trim();
            var categoryName = (cell.getAttribute("data-categoryname") || "").trim();
            var hotelField = document.getElementById("<%= hfHotelIdB64.ClientID %>");
            var hotelId = hotelField ? (hotelField.value || "").trim() : "";

            if (!date || !categoryId || !hotelId) return;

            // CategoryLocalId is used only to query the log. It is never shown.
            availabilityLogText("availabilityLogDate", date);
            availabilityLogText("availabilityLogCategory", categoryName || "Availability");
            availabilityLogText(
                "availabilityLogSubtitle",
                date + (categoryName ? " · " + categoryName : ""));

            availabilityLogSetLoading();
            availabilityLogOpen();

            fetch('<%= ResolveUrl("~/AvailabilitySetup.aspx/GetAvailabilityChangeLog") %>', {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/json; charset=utf-8"
                },
                body: JSON.stringify({
                    hotelId: hotelId,
                    categoryId: categoryId,
                    date: date
                })
            })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }

                return response.json();
            })
            .then(function (payload) {
                availabilityLogRender(payload && payload.d ? payload.d : []);
            })
            .catch(function (error) {
                availabilityLogDisplay("availabilityLogLoading", false);
                availabilityLogDisplay("availabilityLogTimeline", false);
                availabilityLogDisplay("availabilityLogEmpty", false);

                var errorBox = document.getElementById("availabilityLogError");
                if (errorBox) {
                    errorBox.textContent =
                        "Unable to load availability change log. " +
                        (error && error.message ? error.message : "Please try again.");
                    errorBox.style.display = "";
                }
            });
        }

        function bindAvailabilityChangeLog() {
            var root = document.getElementById("<%= maindiv.ClientID %>") || document;
            if (root.__availabilityLogBound === true) return;
            root.__availabilityLogBound = true;

            root.addEventListener("click", function (e) {
                var cell = e.target && e.target.closest
                    ? e.target.closest(".calCell[data-rowtype='availability']")
                    : null;

                if (!cell || !root.contains(cell)) return;

                e.preventDefault();
                e.stopPropagation();
                openAvailabilityChangeLog(cell);
            }, false);

            var closeButton = document.getElementById("availabilityLogClose");
            if (closeButton && closeButton.__availabilityLogCloseBound !== true) {
                closeButton.__availabilityLogCloseBound = true;
                closeButton.addEventListener("click", availabilityLogClose);
            }

            var overlay = document.getElementById("availabilityLogModal");
            if (overlay && overlay.__availabilityLogOutsideBound !== true) {
                overlay.__availabilityLogOutsideBound = true;
                overlay.addEventListener("click", function (e) {
                    if (e.target === overlay) {
                        availabilityLogClose();
                    }
                });
            }

            if (document.__availabilityLogEscapeBound !== true) {
                document.__availabilityLogEscapeBound = true;
                document.addEventListener("keydown", function (e) {
                    if (e.key === "Escape") {
                        availabilityLogClose();
                    }
                });
            }
        }

        document.addEventListener("DOMContentLoaded", bindAvailabilityChangeLog);

        // Rebind after ASP.NET UpdatePanel partial postbacks.
        if (window.Sys && Sys.WebForms && Sys.WebForms.PageRequestManager) {
            Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
                var root = document.getElementById("<%= maindiv.ClientID %>") || document;
                root.__availabilityLogBound = false;
                bindAvailabilityChangeLog();
            });
        }
    })();
</script>


 <script type="text/javascript">
     (function () {
         function autoCloseFrontDeskSidebar() {
             if (!document.body) return;

             if (window.innerWidth > 768) {
                 // Desktop/tablet: use the existing Site.Master collapsed layout.
                 document.body.classList.add('sidebar-collapsed');
             } else {
                 // Mobile: make sure the mobile sidebar is closed.
                 document.body.classList.remove('mobile-sidebar-open');

                 var sidebar = document.getElementById('sidebar');
                 if (sidebar) {
                     sidebar.classList.remove('open');
                 }
             }
         }

         // Site.Master also restores sidebar state during DOMContentLoaded.
         // Run after it so this Calendar page always starts with the sidebar closed.
         window.addEventListener('load', function () {
             setTimeout(autoCloseFrontDeskSidebar, 0);
         });

         // Keep it closed after ASP.NET UpdatePanel partial postbacks as well.
         if (typeof Sys !== 'undefined' &&
             Sys.WebForms &&
             Sys.WebForms.PageRequestManager) {

             Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
                 autoCloseFrontDeskSidebar();
             });
         }

         // Expose only if needed by other Calendar-page scripts.
         window.autoCloseFrontDeskSidebar = autoCloseFrontDeskSidebar;
     })();
 </script>
        </ContentTemplate>
    </asp:UpdatePanel>
</asp:Content> 
                  
