 <%@ Page MasterPageFile="Site.Master" EnableEventValidation="false" Language="C#" AutoEventWireup="true" Inherits="hotelsoftware.Dashboard" Codebehind="Dashboard.aspx.cs" %>
  
<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
<!-- DASHBOARD BUILD: 2026-08-05-OPERATIONS-POPUPS-PAYMENT-FIX-V16

<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css">

<style>
.repeaterheight
{
    height:370px;
    padding:5px;
}
.repeaterheight2
{
    height:520px;
    padding:5px;
}
.repeaterheightforproftloss
{
    height:220px;
    padding:5px;
}
.screenblur 
{
    display:none;
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background-color: rgba(0, 0, 0, 0.5); /* Semi-transparent black */
    backdrop-filter: blur(5px); /* Apply blur effect */
    z-index: 998; /* Ensure overlay is behind the popup */
} 
#dashboardtitle
{
    margin-top:75px;
}
.popupwZcx {
    display: none; 
    position: fixed; 
    padding:0px;
    top: 50%; 
    left: 50%;
    transform: translate(-50%, -50%); 
    height:60%;  
    width: 65%;
    background-color: #ffffff;
    border: 1px solid #ccc;
    border-radius: 5px;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
    z-index: 9999; 
}
.popupwZ {
    display: none; 
    position: fixed; 
    padding:0px;
    top: 50%; 
    left: 50%;
    height:80%; 
    transform: translate(-50%, -50%); 
    width: 65%;
    background-color: #ffffff;
    border: 1px solid #ccc;
    border-radius: 5px;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
    z-index: 9999; 
}
.m-0{
    margin:0px !important;
}
.popupwZc {
    display: none; 
    position: fixed; 
    padding:0px;
    top: 50%; 
    left: 50%; 
    transform: translate(-50%, -50%); 
   height:60%;  
    width: 65%;
    background-color: #ffffff;
    border: 1px solid #ccc;
    border-radius: 5px;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
    z-index: 9999; 
    font-size:14px;
}
.popupwZc table {
    width: 100%;
    background-color: #e9e9e9;
    font-size:14px;
}
 .popupwZc button {
    margin-top: 10px;
    padding: 5px 10px;
    background-color: #4CAF50;
    color: #fff;
    border: none;
    cursor: pointer;
    font-size:14px;
}
.popupwZcy {
    display: none; 
    position: fixed; 
    padding:0px;
    top: 50%; 
    left: 50%; 
    transform: translate(-50%, -50%); 
   height:85%;  
    width: 65%;
    background-color: #ffffff;
    border: 1px solid #ccc;
    border-radius: 5px;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
    z-index: 9999; 
}



.popupwZcy table {
    width: 100%;
    background-color: #e9e9e9;
}

.popupwZcy button {
    margin-top: 10px;
    padding: 5px 10px;
    background-color: #4CAF50;
    color: #fff;
    border: none;
    cursor: pointer;
}


.popupwZ table 
{
    width: 100%;  
}

.popupwZ button 
{
    margin-top: 10px;
    padding: 5px 10px;
    background-color: #4CAF50;
    color: #fff;
    border: none;
    cursor: pointer;
}


.popupw 
{
    display: none; 
    position: fixed; 
    padding:0px;
    top: 50%; 
    left: 50%; 
    transform: translate(-50%, -50%); 
    height:60%;  
    width: 65%;
    background-color: #ffffff;
    border: 1px solid #ccc;
    border-radius: 5px;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
    z-index: 9999; 
}
.popupw table
{
    width: 100%;
    background-color: #e9e9e9;
}
.popupw button 
{
    margin-top: 10px;
    padding: 5px 10px;
    background-color: #4CAF50;
    color: #fff;
    border: none;
    cursor: pointer;
}
.popupw::-webkit-scrollbar 
{
    width: 8px; 
}
.popupw::-webkit-scrollbar-thumb 
{
    background-color: #ccc; 
    border-radius: 4px; 
}
.popupw::-webkit-scrollbar-thumb:hover 
{
    background-color: #999; 
}
.popupw::-webkit-scrollbar-thumb:active 
{
    background-color: #666; 
}
.imagesize 
{
    width: 30px;
    height: 30px;
}
.first 
{
    width: 70%;
}
.display
{
     display:flex;
     justify-content: space-between;
     align-items: center;
     margin:20px;
}
.container 
{
    display: flex;
    justify-content: space-between;
    background: White;
}
.container2 
{
    display: flex;
    justify-content: space-between;
    background: White;
}
.box 
{
    background:white;
    flex: 1;
    margin: 5px;
    padding: 10px;
    padding-top: 20px; 
    border: 1px solid grey;
    border-radius:15px;
    box-sizing: border-box;
    text-align:center;
}
.displayflex 
{
    display: flex;
}
.imagesize 
{
    width: 13px; 
    height: 13px; 
}
 .item {
    border: 1px solid #ddd;
    padding: 2px;
    text-align: center;
    width: 50px;
    height: 50px;
    margin: 5px;
    background-color: #f0f0f0;
    border-radius: 10px;
}

.big-box 
{
    width: 170px;
    height: 40px;
    background-color: #e0e0e0;
    border-radius: 20px;
    font-size: 16px;
    color: #333;
    font-weight: bold;
}
   
    
.display 
{
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin: 5px 20px;
}
 
#Table2 
{
    border-collapse: collapse;
    width: 100%;
   
}

#Table2 th 
{
    background-color:#e4e4e4;
    padding:5px;
    white-space:nowrap;
    font-size:12px;
    font-family:'Montserrat';
    font-weight:bold;
   
}
#Table2 td,tr 
{
    border: 1px solid lightgrey;
    padding-left:2px;
    white-space:nowrap;


}
::-webkit-scrollbar 
{
    width: 5px;
    height: 5px;
}

/* Track */
::-webkit-scrollbar-track 
{
    background: #f1f1f1;
}

/* Handle */
::-webkit-scrollbar-thumb 
{
    background: #e3e4e7;
    border-radius: 40px;
}

/* Handle on hover */
::-webkit-scrollbar-thumb:hover 
{
    background: #555;
}

::-webkit-scrollbar-thumb:hover 
{
    background: #555;
}

/* Handle when the mouse is over it */
::-webkit-scrollbar-thumb:window-inactive 
{
    background: #aaa;
}
.cardst 
{
    padding:5px;
    height: 110px;
    background-color: #ffffff;
    border: 1px solid #d3d3d340;
}

.card-hover:hover 
{
    transition: all 0.7s ease;
    box-shadow: 2px 2px 10px rgba(0, 0, 0, 0.1);
    transform: scale(1.02); /* Enlarge the card */
} 

.card-titlest 
{
    font-size: 16px;
    margin-bottom:-15px;
    padding:10px;
    font-weight:500;
    color:black;
    font-family:'Montserrat';
}

.card-contentst 
{
    font-size: 1rem;
    color: #555555;
    line-height: 1.5;
}
.cardvalue
{
    text-align:center;
    font-size:25px;    
    color: #545454;
    font-weight:bold;
}
.cardvalue1
{
   font-size:15px; 
   text-align:center;
   color: #9b9a9a;
   font-weight:400;
}
.cardstimg
{
    height:35px; 
    margin-right:5px; 
    margin-top:15px;
    width:35px;
    padding:5px;
}

.card-z
{
   border:1px solid #dfdada45;
   box-shadow: 0 0px 0px rgba(0, 0, 0, 0.1);
}
    
.card-valuelabel
{
   text-align:center;
}
.card-percentage
{
    font-size:12px;
}



.btnprintscreen
{
    height:30px;
    width:30px;
    vertical-align:middle;
    margin-left:20px; 
    margin-top:10px;
    visibility:visible;
}
#Table 
{
    border-collapse: collapse;
    width: 100%;
    font-family:'Montserrat';
}

#Table th 
{
    font-size: 13px; 
    padding: 2px;
    background-color: White; 
}

#Table td 
{
    padding: 0px;
}
       
#option1:hover
{   
    background-color: #a8dadc;     
}
#option2:hover 
{   
    background-color: #e5c7c7;     
}
     
.btnshowclass
{
    margin-left:40px;
    display:flex;
}

   
@media only screen and (min-width: 769px) and (max-width: 1280px) {
.container-fluid 
{
    padding: 0 5px; /* Adjust padding for tablet screens */
}
.repeaterheight
{
    height:280px;
    padding:5px;
}
   .repeaterheight2
{
    height:420px;
    padding:5px;
}
.repeaterheightforproftloss
{
    height:170px;
    padding:5px;
}
.cardst 
{
    width: calc(100%); /* Adjust the width of the card */
}

.cardstimg 
{
    height: 30px;
    width: 30px;
}


.card-percentage
{
    font-size:8px;
}
.container 
{
    flex-direction: column;
}

.box 
{
    width: 100%;
}

.card-contentst 
{
    font-size: 10px;
    color: #555555;
    line-height: 1.5;
}



.imagesize 
{
    width: 15px;
    height: 15px;
}
        
}

    
@media only screen and (max-width: 768px)
{
     card-height{
         height:400px;
     }
    .popupwZcx,
    .popupwZ,
    .popupwZc,
    .popupwZcy,
    .popupw
    {
        display: none; 
        position: fixed; 
        padding:0px;
        top: 50%; 
        left: 50%; 
        transform: translate(-50%, -50%); 
        height:100%;  
        width: 100%;
        background-color: #ffffff;
        border: 1px solid #ccc;
        border-radius: 5px;
        box-shadow: 0 2px 5px rgba(0, 0, 0, 0.2); 
        z-index: 9999; 
    }
    .page-header
    {
        font-size: 25px;
      
    }
    .repeaterheight, .repeaterheight2
    {
        height:auto;
        max-height:800px;
        padding:5px;
    }
     
    .repeaterheightforproftloss
    {
        height:295px;
        padding:5px;
    }
     .btnprintscreen
    {
         visibility:hidden;
         display:none;
    }
    .container-fluid 
    {
        padding: 0 5px; 
    }

    .cardst 
    {
        width: 100%;
    }

    .cardstimg 
    {
        height: 25px;
        width: 25px;
    }

    .container 
    {
        margin-top:50%;
        flex-direction: column;
    }

    .box 
    {
        width: 100%;
    }
    #dashboardtitle
    {
       flex-direction: column;
    }
    .startenddate
    {
       flex-direction: column;
    }

    #enddate,txtDate_checkin
    {
        width:100%;
    }
    #ImageButton2
    {
        visible:false;
    }
    .btn-width
    {
        width:100%;
    }

     .staff-container
    {
        padding:0px; 
    }
    fdosummary
    {
        flex-direction: column;
    }
    .big-box 
    {
        width: 144px;
        height:50px;
    }
    .mrgntop
    {
        margin-top:-20px;
    }
    #extra
    {
        display:none;
    }
    .btnshowclass
    {
        margin-left:0px;
    }
}
.perdentagediv
{
    display: none;
    align-item: top;
    margin-left: -18px;
    margin-top: -8px;
}
    table, tr, th, td {
        border: 1px solid BLACK;
        padding:5px;
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
    opacity: 0; /* Default hidden state */
    transition: opacity 0.3s, visibility 0.3s;
}
.page-header:hover .tooltip {
    visibility: visible;
    opacity: 1;
}
.fontstyle
{
    font-size:16px;
}
.m-0
{
    margin:0px;
}
.hover-button {
    border: none; /* No border */
    padding: 10px 20px; /* Padding */
    font-size: 16px; /* Font size */
    cursor: pointer; /* Pointer cursor on hover */
    transition: background-color 0.3s, transform 0.3s; 
    border-bottom: 1px solid lightgray;
    border-radius:0px;
}
.hover-button:hover {
   border: 1px solid lightgray;
   border-radius:8px 8px 0px 0px;
}
#Table6 { /* Ensure the ID matches exactly, including case */
    border: none;
    border-collapse: collapse;
    width: 100%; /* Optional: Make the table full width */
}
 #Table6 th, #Table6 td { /* Add #Table6 to the selectors for clarity */
    border: none; /* Ensure no borders for header and data cells */
}
 #Table6 tr{
      border:none;
      border-bottom: 1px solid lightgray;
 }
  .recording-symbol {
    position: relative;
    width: 10px; /* Adjust the size as needed */
    height: 10px; /* Adjust the size as needed */
    display: flex;
    justify-content: center;
    align-items: center;
  }
  .blinking-dot {
    width: 4px; /* Adjust the size of the dot */
    height:4px; /* Adjust the size of the dot */
    background-color: darkgreen; /* Color of the blinking dot */
    border-radius: 50%; /* Makes the dot round */
    /* Blink animation */
    position: absolute;
  }
  .recording-symbol::before {
    content: '';
    animation: blink 1s infinite;
    position: absolute;
    width: 12px; /* Same as recording-symbol width */
    height: 12px; /* Same as recording-symbol height */
    border: 3px solid #6fd1a4; /* Thin circle layer */
    border-radius: 50%; /* Makes it round */
  }
  @keyframes blink {
    0% {
      opacity: 1;
    }
    50% {
      opacity: 0.2;
    }
    100% {
      opacity: 1;
    }
  }
</style>
     <style>
    .card {
      border-radius: 15px;
      box-shadow: 0 4px 8px rgba(0,0,0,0.05);
      text-align: center;
      padding: 20px;
      margin-bottom:10px;
    }
    .progress-ring {
      position: relative;
      width: 120px;
      height: 120px;
      margin: auto;
    }
    .progress-ring svg {
      transform: rotate(-90deg);
    }
    .progress-ring circle {
      fill: none;
      stroke-width: 12;
      stroke-linecap: round;
    }
    .progress-ring .bg {
      stroke: #d3dce6;
    }
    .progress-ring .progress {
      stroke: #6a5acd; /* default color */
      stroke-dasharray: 339; /* circumference of circle */
      stroke-dashoffset: 339;
      transition: stroke-dashoffset 0.6s ease;
    }
    .progress-text {
      position: absolute;
      top: 50%;
      left: 50%;
      transform: translate(-50%, -50%);
      font-size: 2rem;
      font-weight: bold;
    }
    .progress-subtext {
      font-size: 0.9rem;
      color: #6c757d;
    }
    /* Custom colors */
    .checkin .progress { stroke: #17a2b8; }
    .checkout .progress { stroke: #dc3545; }
   .rooms .progress { stroke: #4b2ecc; }     /* Purple → Available Rooms */

/* Different colors for room categories */
.rooms1 .progress { stroke: #007bff; }    /* Blue → Occupied Rooms */
.rooms2 .progress { stroke: #fd7e14; }    /* Orange → Blocked Rooms */
.rooms3 .progress { stroke: #731515; }    /* Orange → Blocked Rooms */

  </style>


<!-- =========================================================
     EXECUTIVE PMS DASHBOARD DESIGN UPGRADE
     NOTE: UI ONLY. Existing IDs, ASP.NET controls, events,
     popup classes and JavaScript behavior are preserved.
========================================================= -->
<style>
:root{
    --pms-navy:#0B1220;
    --pms-navy-2:#111827;
    --pms-blue:#2563EB;
    --pms-blue-2:#1D4ED8;
    --pms-gold:#F59E0B;
    --pms-green:#10B981;
    --pms-red:#EF4444;
    --pms-purple:#7C3AED;
    --pms-bg:#F3F6FB;
    --pms-card:#FFFFFF;
    --pms-soft:#F8FAFC;
    --pms-border:#E2E8F0;
    --pms-text:#0F172A;
    --pms-muted:#64748B;
    --pms-shadow:0 14px 38px rgba(15,23,42,.08);
    --pms-shadow-lg:0 24px 70px rgba(15,23,42,.18);
}

/* Global surface */
body{
    background:
        radial-gradient(circle at top left, rgba(37,99,235,.08), transparent 32%),
        linear-gradient(180deg,#F8FAFC 0%,#EEF2F7 100%) !important;
    color:var(--pms-text) !important;
    font-family:'Inter','Montserrat',Arial,sans-serif !important;
}

.container-fluid{
    max-width:100% !important;
}

/* Keep square/no-radius style as requested previously */
.card,
.cardst,
.card-z,
.box,
.big-box,
.hover-button,
.form-control,
.dropdownstyle,
.btnform-control,
.popupwZcx,
.popupwZ,
.popupwZc,
.popupwZcy,
.popupw,
.calendar-popup,
.input-group-addon,
button,
.btn{
    border-radius:0 !important;
}

/* Top warning bar */
#warningshow{
    background:#FEF2F2 !important;
    color:#991B1B !important;
    border-left:5px solid var(--pms-red);
    box-shadow:var(--pms-shadow);
    margin-bottom:12px;
}

/* Header/title area */
.page-header{
    font-size:22px !important;
    font-weight:500 !important;
    letter-spacing:-.9px !important;
    color:var(--pms-navy) !important;
    line-height:1.15 !important;
    text-transform:uppercase;
}

#ContentPlaceHolder2_lblDateRange,
label[for],
#lblDateRange{
    color:var(--pms-muted) !important;
    font-weight:800 !important;
    letter-spacing:.04em;
    font-size:12px !important;
}

/* Premium header row */
#ContentPlaceHolder2_txt_dateRange,
.form-control{
    height:32px !important;
    background:#fff !important;
    border:1px solid var(--pms-border) !important;
    box-shadow:0 1px 2px rgba(15,23,42,.04) !important;
    color:var(--pms-text) !important;
    font-weight:normal !important;
}

.form-control:focus{
    border-color:var(--pms-blue) !important;
    box-shadow:0 0 0 4px rgba(37,99,235,.13) !important;
    outline:none !important;
}

.calendar-icon,
.input-group-addon.calendar-icon{
    height:32px !important;
    width:32px !important;
    display:flex !important;
    align-items:center !important;
    justify-content:center !important;
    background:linear-gradient(135deg,var(--pms-gold),#F97316) !important;
    color:#fff !important;
    border:none !important;
    font-size:18px;
}

.btnform-control,
#ContentPlaceHolder2_btnShow{
    min-height:32px !important;
    background:#e3edfe !important;
    color:black !important;
    border:none !important;
    font-weight:normal !important;
    letter-spacing:.08em !important;
    box-shadow:0 12px 24px rgba(37,99,235,.24) !important;
    transition:all .25s ease !important;
}

.btnform-control:hover,
#ContentPlaceHolder2_btnShow:hover{
    transform:translateY(-2px);
    box-shadow:0 18px 34px rgba(37,99,235,.32) !important;
}

/* Executive tab container */
#ContentPlaceHolder2_UpdatePanel2 > .container-fluid.card,
.container-fluid.card{
    background:#FFFFFF !important;
    border:1px solid rgba(226,232,240,.9) !important;
    box-shadow:var(--pms-shadow) !important;
    padding:5px !important;
    margin-top:14px !important;
}

.hover-button{
    background:#F8FAFC !important;
    border:1px solid var(--pms-border) !important;
    border-bottom:3px solid transparent !important;
    color:var(--pms-text) !important;
    font-weight:800 !important;
    transition:all .25s ease !important;
}

.hover-button a{
    color:var(--pms-text) !important;
    text-decoration:none !important;
    font-weight:900 !important;
}

.hover-button:hover{
    background:#FFFFFF !important;
    border-color:#BFDBFE !important;
    border-bottom-color:var(--pms-blue) !important;
    transform:translateY(-2px);
    box-shadow:0 12px 26px rgba(15,23,42,.08) !important;
}

/* KPI cards */
.cardst{
    min-height:122px !important;
    height:auto !important;
    background:
        linear-gradient(180deg,#FFFFFF 0%,#FBFDFF 100%) !important;
    border:1px solid var(--pms-border) !important;
    border-left:5px solid #e3edfe !important;
    box-shadow:0 10px 28px rgba(15,23,42,.065) !important;
    padding:15px 6px !important;
    position:relative !important;
    overflow:hidden !important;
    transition:all .25s ease !important;
}

.cardst:after{
    content:"";
    position:absolute;
    right:-40px;
    top:-40px;
    width:120px;
    height:120px;
    background:radial-gradient(circle,rgba(37,99,235,.10),transparent 65%);
    pointer-events:none;
}

.cardst:hover{
    transform:translateY(-5px) !important;
    border-color:#BFDBFE !important;
    box-shadow:0 22px 50px rgba(15,23,42,.13) !important;
}

.cardstimg{
    width:42px !important;
    height:42px !important;
    padding:9px !important;
    margin:0 !important;
    background:#EFF6FF !important;
    border:1px solid #DBEAFE !important;
    object-fit:contain !important;
    box-shadow:0 8px 18px rgba(37,99,235,.12);
}

.cardvalue{
    font-size:22px !important;
    line-height:1.05 !important;
    font-weight:normal !important;
    color:var(--pms-navy) !important;
    letter-spacing:-1px !important;
}

.cardvalue1{
    display:block !important;
    margin-top:8px !important;
    font-size:13px !important;
    font-weight:normal !important;
    color:var(--pms-muted) !important;
    text-transform:uppercase;
    letter-spacing:.045em;
}

.card-percentage{
    font-size:11px !important;
    font-weight:900 !important;
    color:var(--pms-green) !important;
}

.perdentagediv{
    margin-left:0 !important;
    margin-top:0 !important;
    align-items:center !important;
    gap:4px;
}

.imagesize{
    width:16px !important;
    height:16px !important;
}

/* Progress cards */
.card{
    background:#FFFFFF !important;
    border:1px solid var(--pms-border) !important;
    box-shadow:0 10px 30px rgba(15,23,42,.07) !important;
    transition:all .25s ease !important;
}

.card:hover{
    box-shadow:0 20px 46px rgba(15,23,42,.12) !important;
    transform:translateY(-3px);
}

.card h6{
    font-size:13px !important;
    font-weight:normal !important;
    color:var(--pms-navy) !important;
    letter-spacing:.03em;
    text-transform:capitalize;
    margin-bottom:3px !important;
    font-family: 'Montserrat', sans-serif !important;
}

.progress-ring{
    width:124px !important;
    height:124px !important;
}

.progress-ring .bg{
    stroke:#E5EAF2 !important;
}

.progress-ring .progress{
    stroke-width:13 !important;
    filter:drop-shadow(0 5px 14px rgba(37,99,235,.25));
}

.progress-text{
    font-size:30px !important;
    font-weight:normal !important;
    color:var(--pms-navy) !important;
}

.progress-subtext{
    font-size:13px !important;
    color:var(--pms-muted) !important;
    font-weight:800 !important;
    margin-top:8px !important;
}

.checkin .progress{stroke:#0891B2 !important;}
.checkout .progress{stroke:#EF4444 !important;}
.rooms .progress{stroke:#2563EB !important;}
.rooms1 .progress{stroke:#7C3AED !important;}
.rooms2 .progress{stroke:#F59E0B !important;}
.rooms3 .progress{stroke:#92400E !important;}

/* Chart panels */
.card-z{
    border:1px solid var(--pms-border) !important;
    background:#FFFFFF !important;
    box-shadow:var(--pms-shadow) !important;
    overflow:hidden !important;
}

.card-z .card-body{
    padding:24px !important;
}

.card-titlest{
    display:inline-flex !important;
    align-items:center !important;
    gap:10px !important;
    font-size:21px !important;
    font-weight:900 !important;
    color:var(--pms-navy) !important;
    letter-spacing:-.6px !important;
    padding:0 !important;
    margin:0 !important;
}

.card-titlest:before{
    content:"";
    width:5px;
    height:24px;
    background:linear-gradient(180deg,var(--pms-blue),var(--pms-gold));
    display:inline-block;
}

.dropdownstyle{
    width:auto !important;
    min-width:86px !important;
    height:38px !important;
    padding:0 12px !important;
    background:#F8FAFC !important;
    border:1px solid var(--pms-border) !important;
    color:var(--pms-text) !important;
    font-weight:900 !important;
    font-size:13px !important;
}

canvas,
#myBarChart1{
    background:
        linear-gradient(180deg,#FFFFFF 0%,#FBFDFF 100%) !important;
}

hr{
    border:0 !important;
    border-top:1px solid var(--pms-border) !important;
    margin:18px 0 !important;
}

/* Tables inside page and popups */
table{
    border-collapse:separate !important;
    border-spacing:0 !important;
}

#Table,
#Table2,
#Table6,
.popupwZ table,
.popupwZc table,
.popupwZcy table,
.popupw table,
.popupwZcx table{
    width:100% !important;
    background:#FFFFFF !important;
    border:1px solid var(--pms-border) !important;
}

#Table th,
#Table2 th,
#Table6 th,
.popupwZ table th,
.popupwZc table th,
.popupwZcy table th,
.popupw table th,
.popupwZcx table th{
    background:linear-gradient(135deg,var(--pms-navy),var(--pms-navy-2)) !important;
    color:#FFFFFF !important;
    border:none !important;
    padding:13px 12px !important;
    font-size:12px !important;
    font-weight:900 !important;
    letter-spacing:.04em;
    text-transform:uppercase;
}

#Table td,
#Table2 td,
#Table6 td,
.popupwZ table td,
.popupwZc table td,
.popupwZcy table td,
.popupw table td,
.popupwZcx table td{
    border:none !important;
    border-bottom:1px solid #EDF2F7 !important;
    padding:11px 12px !important;
    font-size:13px !important;
    color:#334155 !important;
    vertical-align:middle !important;
}

#Table tr:hover td,
#Table2 tr:hover td,
#Table6 tr:hover td,
.popupwZ table tr:hover td,
.popupwZc table tr:hover td,
.popupwZcy table tr:hover td,
.popupw table tr:hover td{
    background:#F8FAFC !important;
}

/* Popup system - keeps display:none and class names intact */
.screenblur{
    background:rgba(11,18,32,.50) !important;
    backdrop-filter:blur(12px) saturate(130%) !important;
    -webkit-backdrop-filter:blur(12px) saturate(130%) !important;
    z-index:998 !important;
}

.popupwZcx,
.popupwZ,
.popupwZc,
.popupwZcy,
.popupw{
    background:#FFFFFF !important;
    border:1px solid rgba(226,232,240,.95) !important;
    box-shadow:var(--pms-shadow-lg) !important;
    overflow:auto !important;
    padding:0 !important;
    animation:pmsPopupIn .22s ease-out;
}

@keyframes pmsPopupIn{
    from{opacity:0; transform:translate(-50%,-46%) scale(.985);}
    to{opacity:1; transform:translate(-50%,-50%) scale(1);}
}

.popupwZ:before,
.popupwZc:before,
.popupwZcy:before,
.popupw:before,
.popupwZcx:before{
    content:"";
    display:block;
    height:6px;
    background:linear-gradient(90deg,var(--pms-blue),var(--pms-gold),var(--pms-green));
    position:sticky;
    top:0;
    z-index:3;
}

.popupwZ button,
.popupwZc button,
.popupwZcy button,
.popupw button,
.popupwZcx button,
.calendar-actions button{
    background:linear-gradient(135deg,var(--pms-blue),var(--pms-blue-2)) !important;
    color:#FFFFFF !important;
    border:none !important;
    padding:10px 18px !important;
    font-weight:900 !important;
    letter-spacing:.03em;
    box-shadow:0 12px 24px rgba(37,99,235,.22) !important;
    transition:all .22s ease !important;
}

.popupwZ button:hover,
.popupwZc button:hover,
.popupwZcy button:hover,
.popupw button:hover,
.popupwZcx button:hover,
.calendar-actions button:hover{
    transform:translateY(-2px);
    box-shadow:0 18px 34px rgba(37,99,235,.32) !important;
}

.popupwZ input,
.popupwZc input,
.popupwZcy input,
.popupw input,
.popupwZcx input,
.popupwZ select,
.popupwZc select,
.popupwZcy select,
.popupw select,
.popupwZcx select,
.popupwZ textarea,
.popupwZc textarea,
.popupwZcy textarea,
.popupw textarea,
.popupwZcx textarea{
    border:1px solid var(--pms-border) !important;
    background:#FFFFFF !important;
    color:var(--pms-text) !important;
    min-height:38px;
    padding:8px 10px !important;
    outline:none !important;
}

.popupwZ input:focus,
.popupwZc input:focus,
.popupwZcy input:focus,
.popupw input:focus,
.popupwZcx input:focus,
.popupwZ select:focus,
.popupwZc select:focus,
.popupwZcy select:focus,
.popupw select:focus,
.popupwZcx select:focus,
.popupwZ textarea:focus,
.popupwZc textarea:focus,
.popupwZcy textarea:focus,
.popupw textarea:focus,
.popupwZcx textarea:focus{
    border-color:var(--pms-blue) !important;
    box-shadow:0 0 0 4px rgba(37,99,235,.12) !important;
}

/* Calendar popup */
.calendar-popup{
    background:#FFFFFF !important;
    border:1px solid var(--pms-border) !important;
    box-shadow:var(--pms-shadow-lg) !important;
}

.calendar-header{
    background:var(--pms-navy) !important;
    color:#FFFFFF !important;
    padding:12px !important;
}

.nav-btn{
    background:rgba(255,255,255,.10) !important;
    color:#FFFFFF !important;
    border:1px solid rgba(255,255,255,.18) !important;
}

/* Tooltips */
.tooltip,
.info-icon::after{
    background:var(--pms-navy) !important;
    color:#fff !important;
    border:1px solid rgba(255,255,255,.12);
    box-shadow:0 14px 30px rgba(15,23,42,.22);
}

/* Live recording dot */
.recording-symbol::before{
    border-color:rgba(16,185,129,.35) !important;
}
.blinking-dot{
    background:var(--pms-green) !important;
    box-shadow:0 0 0 4px rgba(16,185,129,.12);
}

/* Scrollbars */
::-webkit-scrollbar{
    width:8px !important;
    height:8px !important;
}
::-webkit-scrollbar-track{
    background:#F1F5F9 !important;
}
::-webkit-scrollbar-thumb{
    background:#CBD5E1 !important;
    border-radius:0 !important;
}
::-webkit-scrollbar-thumb:hover{
    background:#94A3B8 !important;
}

/* Mobile/tablet */
@media only screen and (max-width: 768px){
    .page-header{
        font-size:21px !important;
    }

    .cardst{
        min-height:110px !important;
        padding:14px 12px !important;
    }

    .cardvalue{
        font-size:25px !important;
    }

    .cardvalue1{
        font-size:11px !important;
    }

    .popupwZcx,
    .popupwZ,
    .popupwZc,
    .popupwZcy,
    .popupw{
        width:100% !important;
        height:100% !important;
        top:50% !important;
        left:50% !important;
        transform:translate(-50%,-50%) !important;
    }

    .card-z .card-body{
        padding:16px !important;
    }

    .card-titlest{
        font-size:17px !important;
    }

    #ContentPlaceHolder2_btnShow{
        width:100% !important;
    }
}
</style>



<style>
/* =========================================================
   Dashboard In-House Guests Widget
   Professional compact grid - no bold text
========================================================= */
.dashboard-inhouse-card{
    background:#fff;
    border:1px solid #e5e7eb;
    box-shadow:0 10px 28px rgba(15,23,42,.065);
    overflow:hidden;
    margin-bottom:10px;
}
.dashboard-inhouse-titlebar{
    display:flex;
    align-items:center;
    justify-content:space-between;
    padding:10px 12px;
    border-bottom:1px solid #edf2f7;
    background:linear-gradient(180deg,#ffffff 0%,#f8fafc 100%);
}
.dashboard-inhouse-title{
    display:flex;
    align-items:center;
    gap:8px;
    margin:0;
    font-size:14px;
    font-weight:400 !important;
    color:#111827;
    letter-spacing:.01em;
}
.dashboard-inhouse-title:before{
    content:"";
    width:4px;
    height:20px;
    background:linear-gradient(180deg,#2563eb,#f59e0b);
    display:inline-block;
}
.dashboard-inhouse-count{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    min-width:26px;
    height:22px;
    padding:0 8px;
    background:#eff6ff;
    border:1px solid #dbeafe;
    color:#1d4ed8;
    font-size:11px;
    font-weight:400 !important;
}
.dashboard-inhouse-scroll{
    max-height:360px;
    overflow:auto;
}
.dashboard-inhouse-grid{
    width:100%;
    border-collapse:collapse !important;
    border-spacing:0 !important;
    margin:0;
}
.dashboard-inhouse-grid th{
    position:sticky;
    top:0;
    z-index:2;
    background:#f8fafc !important;
    color:#64748b !important;
    font-size:10.5px !important;
    font-weight:400 !important;
    padding:8px 9px !important;
    border:0 !important;
    border-bottom:1px solid #e5e7eb !important;
    white-space:nowrap;
    text-align:left;
    text-transform:none !important;
    letter-spacing:0 !important;
}
.dashboard-inhouse-grid td{
    color:#334155 !important;
    font-size:10.5px !important;
    font-weight:400 !important;
    padding:7px 9px !important;
    border:0 !important;
    border-bottom:1px solid #f1f5f9 !important;
    vertical-align:middle;
    white-space:nowrap;
}
.dashboard-inhouse-grid tr:nth-child(even) td{
    background:#fbfdff;
}
.dashboard-inhouse-grid tr:hover td{
    background:#f8fafc !important;
}
.dashboard-inhouse-guest{
    color:#0f172a !important;
    font-weight:400 !important;
}
.dashboard-inhouse-muted{
    color:#64748b !important;
    font-weight:400 !important;
}
.dashboard-room-badge{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    min-width:34px;
    padding:2px 6px;
    background:#eef2ff;
    border:1px solid #e0e7ff;
    color:#3730a3;
    font-size:10px;
    font-weight:400 !important;
}
.dashboard-status-badge{
    display:inline-flex;
    align-items:center;
    padding:2px 7px;
    background:#ecfdf5;
    border:1px solid #d1fae5;
    color:#047857;
    font-size:10px;
    font-weight:400 !important;
}
.dashboard-inhouse-empty{
    padding:22px 10px;
    color:#64748b;
    font-size:11px;
    font-weight:400 !important;
    text-align:center;
}
@media(max-width:768px){
    .dashboard-inhouse-scroll{max-height:420px;}
    .dashboard-inhouse-grid th,
    .dashboard-inhouse-grid td{font-size:10px !important; padding:7px 8px !important;}
}
</style>


<style>
/* =========================================================
   Dashboard In-House Guest Action Dropdown
   Same action style as CustomerDetails page
========================================================= */
.dashboard-inhouse-grid th.dashboard-action-col,
.dashboard-inhouse-grid td.dashboard-action-col{
    text-align:center !important;
    width:120px !important;
    min-width:120px !important;
}
.btn-actions {
    height:30px;
    padding:0 13px!important;
    font-size:11px!important;
    font-weight:900;
    border-radius:999px!important;
    line-height:28px;
    background:linear-gradient(180deg,#2563eb,#1d4ed8)!important;
    color:#fff!important;
    border:1px solid #1d4ed8!important;
    box-shadow:0 7px 16px rgba(37,99,235,.25);
    transition:all .18s ease;
    min-width:unset !important;
}
.btn-actions:hover,
.btn-actions:focus {
    background:linear-gradient(180deg,#1d4ed8,#1e40af)!important;
    color:#fff!important;
    transform:translateY(-1px);
    box-shadow:0 10px 22px rgba(37,99,235,.35);
}
.btn-actions.dropdown-toggle::after {
    margin-left:7px;
    vertical-align:middle;
}
.action-menu {
    border:none!important;
    border-radius:18px!important;
    padding:10px!important;
    min-width:230px;
    box-shadow:0 18px 45px rgba(15,23,42,.20)!important;
    background:#fff;
    z-index:30000!important;
}
.action-menu::before {
    content:"";
    position:absolute;
    top:-8px;
    right:22px;
    width:16px;
    height:16px;
    background:#fff;
    transform:rotate(45deg);
    box-shadow:-3px -3px 8px rgba(15,23,42,.04);
}
.action-item {
    display:flex!important;
    align-items:center;
    gap:12px;
    padding:11px 13px!important;
    border-radius:13px!important;
    font-size:13px!important;
    font-weight:normal !important;
    color:#334155!important;
    transition:all .18s ease;
    position:relative;
    z-index:1;
    text-decoration:none!important;
}
.action-item i {
    width:20px;
    height:20px;
    display:inline-flex;
    align-items:center;
    justify-content:center;
    font-size:16px;
}
.action-item:hover {
    transform:translateX(4px);
    background:#f8fafc!important;
    color:#0f172a!important;
}
.checkin-item i { color:#2563eb; }
.checkin-item:hover { background:#eff6ff!important; }
.receipt-item i { color:#b45309; }
.receipt-item:hover { background:#fffbeb!important; }
.invoice-item i { color:#7c3aed; }
.invoice-item:hover { background:#f5f3ff!important; }
.history-item i { color:#475569; }
.history-item:hover { background:#f1f5f9!important; }
@media only screen and (max-width:768px){
    .dashboard-inhouse-grid th.dashboard-action-col,
    .dashboard-inhouse-grid td.dashboard-action-col{
        min-width:130px!important;
    }
}
</style>


<!-- =========================================================
     INLINE EXECUTIVE DASHBOARD DESIGN V2
     Kept inside Dashboard.aspx. No external custom CSS file.
========================================================= -->
<style>
:root{
    --ora-bg:#f4f7fb;
    --ora-card:#ffffff;
    --ora-text:#172033;
    --ora-muted:#6b778c;
    --ora-border:#e3e8ef;
    --ora-blue:#315b8a;
    --ora-green:#118267;
    --ora-red:#c84444;
    --ora-amber:#ba7418;
    --ora-purple:#7556b8;
    --ora-shadow:0 5px 18px rgba(23,32,51,.055);
}

/* Main page surface */
body{
    background:var(--ora-bg) !important;
    color:var(--ora-text) !important;
    font-family:Inter,"Segoe UI",Arial,sans-serif !important;
}

#aspnetForm{
    background:var(--ora-bg) !important;
}

#ContentPlaceHolder2_UpdatePanel2 > .container-fluid.card{
    padding:0 !important;
    margin-top:12px !important;
    background:transparent !important;
    border:0 !important;
    box-shadow:none !important;
}

/* Header */
.page-header{
    margin:0 !important;
    padding-top:4px !important;
    color:var(--ora-text) !important;
    font-size:24px !important;
    font-weight:650 !important;
    letter-spacing:-.35px !important;
    text-transform:none !important;
}

#ContentPlaceHolder2_lblDateRange{
    color:#435166 !important;
    font-size:11px !important;
    font-weight:600 !important;
}

#ContentPlaceHolder2_txt_dateRange{
    height:36px !important;
    border:1px solid #ccd5df !important;
    background:#fff !important;
    color:#344054 !important;
    font-size:12px !important;
    box-shadow:none !important;
}

.calendar-icon,
.input-group-addon.calendar-icon{
    width:36px !important;
    height:36px !important;
    background:var(--ora-blue) !important;
    color:#fff !important;
    border:0 !important;
    display:flex !important;
    align-items:center !important;
    justify-content:center !important;
}

#ContentPlaceHolder2_btnShow{
    height:36px !important;
    min-width:110px !important;
    margin-top:5px !important;
    padding:0 16px !important;
    background:var(--ora-blue) !important;
    color:#fff !important;
    border:1px solid var(--ora-blue) !important;
    box-shadow:none !important;
    font-size:11px !important;
    font-weight:600 !important;
    letter-spacing:.3px !important;
}

/* Remove empty hidden navigation strip */
#ContentPlaceHolder2_UpdatePanel2 > .container-fluid.card > .row:first-child{
    display:none !important;
}

/* Section wrappers */
[id$="topfinancialdiv"],
[id$="topReservationdiv"]{
    display:block !important;
    padding:0 !important;
    margin:0 0 12px 0 !important;
    background:transparent !important;
}

[id$="topfinancialdiv"]::before,
[id$="topReservationdiv"]::before{
    display:block;
    margin:0 0 7px 0;
    color:var(--ora-text);
    font-size:13px;
    font-weight:650;
}

[id$="topfinancialdiv"]::before{
    content:"Financial Overview";
}

[id$="topReservationdiv"]::before{
    content:"Reservation Performance";
}

[id$="topfinancialdiv"] > .row{
    display:grid !important;
    grid-template-columns:repeat(5,minmax(0,1fr)) !important;
    gap:8px !important;
    margin:0 !important;
}

[id$="topReservationdiv"] > .row{
    display:grid !important;
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
    gap:8px !important;
    margin:0 !important;
}

/* Override Bootstrap widths inside KPI grids */
[id$="topfinancialdiv"] > .row > [class*="col-"],
[id$="topReservationdiv"] > .row > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    margin:0 !important;
    padding:0 !important;
}

/* Compact executive KPI cards */
[id$="topfinancialdiv"] .cardst,
[id$="topReservationdiv"] .cardst{
    min-height:108px !important;
    height:108px !important;
    padding:12px 13px !important;
    margin:0 !important;
    background:#fff !important;
    border:1px solid var(--ora-border) !important;
    border-left:4px solid var(--ora-blue) !important;
    box-shadow:var(--ora-shadow) !important;
    position:relative !important;
    overflow:hidden !important;
    transition:transform .18s ease, box-shadow .18s ease !important;
}

[id$="topfinancialdiv"] .cardst::after,
[id$="topReservationdiv"] .cardst::after{
    content:"";
    position:absolute;
    width:76px;
    height:76px;
    right:-24px;
    top:-24px;
    border-radius:50%;
    background:rgba(49,91,138,.055);
    pointer-events:none;
}

[id$="topfinancialdiv"] > .row > div:nth-child(2) .cardst,
[id$="topReservationdiv"] > .row > div:nth-child(2) .cardst{
    border-left-color:var(--ora-amber) !important;
}

[id$="topfinancialdiv"] > .row > div:nth-child(3) .cardst{
    border-left-color:var(--ora-purple) !important;
}

[id$="topfinancialdiv"] > .row > div:nth-last-child(2) .cardst{
    border-left-color:var(--ora-red) !important;
}

[id$="topfinancialdiv"] > .row > div:last-child .cardst{
    border-left-color:var(--ora-green) !important;
}

[id$="topReservationdiv"] > .row > div:nth-child(3) .cardst{
    border-left-color:var(--ora-amber) !important;
}

[id$="topReservationdiv"] > .row > div:nth-child(4) .cardst{
    border-left-color:var(--ora-red) !important;
}

[id$="topfinancialdiv"] .card-hover:hover,
[id$="topReservationdiv"] .card-hover:hover{
    transform:translateY(-2px) !important;
    box-shadow:0 10px 25px rgba(23,32,51,.10) !important;
}

/* Inner card content */
[id$="topfinancialdiv"] .cardst > div:first-child,
[id$="topReservationdiv"] .cardst > div:first-child{
    align-items:flex-start !important;
}

[id$="topfinancialdiv"] .cardstimg,
[id$="topReservationdiv"] .cardstimg{
    width:32px !important;
    height:32px !important;
    padding:7px !important;
    margin:0 !important;
    background:#edf4fb !important;
    border:1px solid #dce8f5 !important;
    box-shadow:none !important;
    object-fit:contain !important;
}

[id$="topfinancialdiv"] .card-valuelabel,
[id$="topReservationdiv"] .card-valuelabel{
    flex:1 !important;
    padding:0 5px !important;
}

[id$="topfinancialdiv"] .card-valuelabel > span,
[id$="topReservationdiv"] .card-valuelabel > span{
    font-size:15px !important;
    color:var(--ora-text) !important;
}

[id$="topfinancialdiv"] .cardvalue,
[id$="topReservationdiv"] .cardvalue{
    width:auto !important;
    color:var(--ora-text) !important;
    font-size:21px !important;
    line-height:1.1 !important;
    font-weight:650 !important;
    letter-spacing:-.25px !important;
    text-align:center !important;
}

[id$="topfinancialdiv"] .cardvalue1,
[id$="topReservationdiv"] .cardvalue1{
    display:block !important;
    margin-top:8px !important;
    color:var(--ora-muted) !important;
    font-size:10px !important;
    line-height:1.25 !important;
    font-weight:600 !important;
    letter-spacing:.3px !important;
    text-transform:uppercase !important;
    white-space:normal !important;
}

[id$="topfinancialdiv"] .perdentagediv,
[id$="topReservationdiv"] .perdentagediv{
    display:none !important;
}

[id$="topfinancialdiv"] .recording-symbol,
[id$="topReservationdiv"] .recording-symbol{
    width:10px !important;
    min-width:10px !important;
    margin:0 !important;
}

/* Operational ring cards */
.p-col{
    padding:4px !important;
}

.p-col > .card{
    min-height:174px !important;
    height:174px !important;
    padding:12px !important;
    margin:0 !important;
    background:#fff !important;
    border:1px solid var(--ora-border) !important;
    box-shadow:var(--ora-shadow) !important;
}

.p-col > .card h6{
    margin:0 0 8px !important;
    color:var(--ora-text) !important;
    font-size:11px !important;
    font-weight:650 !important;
    text-transform:none !important;
    letter-spacing:0 !important;
}

.progress-ring{
    width:96px !important;
    height:96px !important;
}

.progress-ring svg{
    width:96px !important;
    height:96px !important;
}

.progress-ring svg circle{
    cx:48;
    cy:48;
    r:40;
    stroke-width:10 !important;
}

.progress-text{
    font-size:22px !important;
    font-weight:650 !important;
    color:var(--ora-text) !important;
}

.progress-subtext{
    margin-top:8px !important;
    color:var(--ora-muted) !important;
    font-size:11px !important;
    font-weight:400 !important;
}

.progress-subtext span,
.progress-subtext .aspNetDisabled{
    font-size:13px !important;
}

/* Chart and table panels */
.card-z,
.dashboard-inhouse-card{
    background:#fff !important;
    border:1px solid var(--ora-border) !important;
    box-shadow:var(--ora-shadow) !important;
}

.card-z .card-body{
    padding:13px !important;
}

.card-titlest,
.dashboard-inhouse-title{
    color:var(--ora-text) !important;
    font-size:13px !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
}

.card-titlest::before{
    display:none !important;
}

.dashboard-inhouse-titlebar{
    padding:11px 13px !important;
    background:#fbfcfe !important;
    border-bottom:1px solid var(--ora-border) !important;
}

.dashboard-inhouse-title::before{
    width:3px !important;
    height:17px !important;
    background:var(--ora-blue) !important;
}

.dashboard-inhouse-count{
    background:#edf4fb !important;
    border:1px solid #dce8f5 !important;
    color:var(--ora-blue) !important;
}

.dashboard-inhouse-grid th{
    background:#f7f9fc !important;
    color:#59677a !important;
    font-size:10px !important;
    font-weight:600 !important;
    padding:8px 9px !important;
}

.dashboard-inhouse-grid td{
    color:#344054 !important;
    font-size:10.5px !important;
    padding:8px 9px !important;
}

/* Responsive behavior */
@media(max-width:1350px){
    [id$="topfinancialdiv"] > .row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }

    [id$="topReservationdiv"] > .row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}

@media(max-width:800px){
    [id$="topfinancialdiv"] > .row,
    [id$="topReservationdiv"] > .row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }

    [id$="topfinancialdiv"] .cardst,
    [id$="topReservationdiv"] .cardst{
        height:112px !important;
        min-height:112px !important;
    }
}

@media(max-width:520px){
    [id$="topfinancialdiv"] > .row,
    [id$="topReservationdiv"] > .row{
        grid-template-columns:1fr !important;
    }
}
</style>


<!-- =========================================================
     TODAY'S OPERATIONS - LIVE COMPACT DESIGN V3
     Only the six live operation cards are affected.
========================================================= -->
<style>
/* Keep this section separate from the rest of the dashboard */
.ora-today-operations-title{
    display:flex;
    justify-content:space-between;
    align-items:center;
    margin:12px 0 7px 0;
}
.ora-today-operations-title h5{
    margin:0;
    color:#172033;
    font-size:13px;
    font-weight:650;
    letter-spacing:0;
}
.ora-today-operations-title span{
    color:#6b778c;
    font-size:10px;
}

/* Six cards in one clean row */
.ora-live-operations-row{
    display:grid !important;
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
    gap:8px !important;
    margin:0 !important;
}
.ora-live-operations-row > .p-col{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    padding:0 !important;
}
.ora-live-operations-row > .p-col > .card{
    height:174px !important;
    min-height:174px !important;
    margin:0 !important;
    padding:12px !important;
    background:#fff !important;
    border:1px solid #e3e8ef !important;
    border-radius:0 !important;
    box-shadow:0 5px 18px rgba(23,32,51,.055) !important;
    text-align:center !important;
    overflow:hidden !important;
}
.ora-live-operations-row > .p-col > .card h6{
    margin:0 0 8px 0 !important;
    color:#172033 !important;
    font-size:11px !important;
    font-weight:650 !important;
    line-height:1.2 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
}

/* Ring size matches the supplied HTML */
.ora-live-operations-row .progress-ring{
    position:relative !important;
    width:95px !important;
    height:95px !important;
    margin:0 auto !important;
}
.ora-live-operations-row .progress-ring svg{
    display:block !important;
    width:95px !important;
    height:95px !important;
    margin:0 auto !important;
}
.ora-live-operations-row .progress-ring svg circle{
    stroke-width:10 !important;
}
.ora-live-operations-row .progress-ring .bg{
    stroke:#e9edf2 !important;
}
.ora-live-operations-row .progress-text{
    position:absolute !important;
    top:50% !important;
    left:50% !important;
    transform:translate(-50%,-50%) !important;
    color:#172033 !important;
    font-size:22px !important;
    font-weight:650 !important;
    line-height:1 !important;
}
.ora-live-operations-row .progress-subtext{
    margin-top:8px !important;
    color:#6b778c !important;
    font-size:11px !important;
    font-weight:400 !important;
}
.ora-live-operations-row .progress-subtext span,
.ora-live-operations-row .progress-subtext label{
    color:#172033 !important;
    font-size:12px !important;
    font-weight:600 !important;
}

/* Exact clean operational colors */
.ora-live-operations-row .checkin .progress{stroke:#1490a8 !important;}
.ora-live-operations-row .checkout .progress{stroke:#d34b4b !important;}
.ora-live-operations-row .rooms .progress{stroke:#315b8a !important;}
.ora-live-operations-row .rooms1 .progress{stroke:#7556b8 !important;}
.ora-live-operations-row .rooms2 .progress{stroke:#ba7418 !important;}
.ora-live-operations-row .rooms3 .progress{stroke:#7f2828 !important;}

.ora-live-operations-row .info-icon{
    color:#8a8f97 !important;
    font-size:12px !important;
    margin-left:5px !important;
}

/* Responsive without affecting other dashboard sections */
@media(max-width:1250px){
    .ora-live-operations-row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}
@media(max-width:700px){
    .ora-live-operations-row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
}
@media(max-width:430px){
    .ora-live-operations-row{
        grid-template-columns:1fr !important;
    }
}
</style>


<!-- =========================================================
     FULL PROTOTYPE LIVE DESIGN V4
     Complete page design controlled inline in Dashboard.aspx.
========================================================= -->
<style>
:root{
    --v4-bg:#f4f7fb;
    --v4-card:#fff;
    --v4-text:#172033;
    --v4-muted:#6b778c;
    --v4-line:#e3e8ef;
    --v4-blue:#315b8a;
    --v4-green:#118267;
    --v4-red:#c84444;
    --v4-amber:#ba7418;
    --v4-purple:#7556b8;
    --v4-shadow:0 5px 18px rgba(23,32,51,.055);
}

body,
#aspnetForm{
    background:var(--v4-bg) !important;
    color:var(--v4-text) !important;
    font-family:Inter,"Segoe UI",Arial,sans-serif !important;
}

#ContentPlaceHolder2_UpdatePanel2{
    display:block !important;
}

/* Page spacing and header exactly like prototype */
#ContentPlaceHolder2_UpdatePanel2 + style,
#ContentPlaceHolder2_UpdatePanel2 ~ style{
    display:none;
}

#ContentPlaceHolder2_UpdatePanel2 > .container-fluid.card{
    max-width:none !important;
    margin:12px 0 0 0 !important;
    padding:0 !important;
    background:transparent !important;
    border:0 !important;
    box-shadow:none !important;
}

.page-header{
    margin:0 !important;
    padding:0 !important;
    color:var(--v4-text) !important;
    font-size:24px !important;
    line-height:1.1 !important;
    font-weight:650 !important;
    letter-spacing:-.3px !important;
    text-transform:none !important;
}

#ContentPlaceHolder2_lblDateRange{
    color:#435166 !important;
    font-size:10px !important;
    font-weight:600 !important;
    text-transform:uppercase !important;
}

#ContentPlaceHolder2_txt_dateRange{
    height:36px !important;
    padding:0 10px !important;
    border:1px solid #ccd5df !important;
    background:#fff !important;
    color:#344054 !important;
    box-shadow:none !important;
    font-size:12px !important;
}

.calendar-icon,
.input-group-addon.calendar-icon{
    width:36px !important;
    height:36px !important;
    border:0 !important;
    background:var(--v4-blue) !important;
    color:#fff !important;
    display:flex !important;
    align-items:center !important;
    justify-content:center !important;
}

#ContentPlaceHolder2_btnShow{
    height:36px !important;
    min-width:110px !important;
    margin-top:5px !important;
    padding:0 14px !important;
    border:1px solid var(--v4-blue) !important;
    background:var(--v4-blue) !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:11px !important;
    font-weight:600 !important;
}

/* Remove old hidden tab/navigation row */
#ContentPlaceHolder2_UpdatePanel2 > .container-fluid.card > .row:first-child{
    display:none !important;
}

/* Financial section exactly as prototype: 7 live cards */
[id$="topfinancialdiv"]{
    display:block !important;
    padding:0 !important;
    margin:0 0 12px 0 !important;
    background:transparent !important;
}

[id$="topfinancialdiv"]::before{
    content:"Financial Overview";
    display:flex;
    align-items:center;
    height:24px;
    margin:0 0 7px 0;
    color:var(--v4-text);
    font-size:13px;
    font-weight:650;
}

[id$="topfinancialdiv"] > .row{
    display:grid !important;
    grid-template-columns:repeat(7,minmax(0,1fr)) !important;
    gap:8px !important;
    margin:0 !important;
}

[id$="topfinancialdiv"] > .row > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    margin:0 !important;
    padding:0 !important;
}

[id$="topfinancialdiv"] .cardst{
    min-height:104px !important;
    height:104px !important;
    margin:0 !important;
    padding:12px !important;
    background:#fff !important;
    border:1px solid var(--v4-line) !important;
    border-left:4px solid var(--v4-blue) !important;
    border-radius:0 !important;
    box-shadow:var(--v4-shadow) !important;
    position:relative !important;
    overflow:hidden !important;
}

[id$="topfinancialdiv"] .cardst::after{
    content:"";
    position:absolute;
    width:75px;
    height:75px;
    right:-22px;
    top:-22px;
    border-radius:50%;
    background:rgba(49,91,138,.055);
    pointer-events:none;
}

[id$="topfinancialdiv"] > .row > div:nth-child(2) .cardst{border-left-color:var(--v4-amber) !important;}
[id$="topfinancialdiv"] > .row > div:nth-child(3) .cardst{border-left-color:var(--v4-purple) !important;}
[id$="topfinancialdiv"] > .row > div:nth-child(4) .cardst{border-left-color:var(--v4-red) !important;}
[id$="topfinancialdiv"] > .row > div:nth-child(5) .cardst{border-left-color:var(--v4-green) !important;}
[id$="topfinancialdiv"] .ora-kpi-noshow .cardst{border-left-color:var(--v4-amber) !important;}
[id$="topfinancialdiv"] .ora-kpi-cancel .cardst{border-left-color:var(--v4-red) !important;}

[id$="topfinancialdiv"] .cardst > div:first-child{
    align-items:flex-start !important;
}

[id$="topfinancialdiv"] .cardstimg{
    width:32px !important;
    height:32px !important;
    margin:0 !important;
    padding:7px !important;
    background:#edf4fb !important;
    border:1px solid #dce8f5 !important;
    box-shadow:none !important;
    object-fit:contain !important;
}

[id$="topfinancialdiv"] .card-valuelabel{
    flex:1 !important;
    padding:0 5px !important;
    text-align:center !important;
}

[id$="topfinancialdiv"] .card-valuelabel > span{
    color:var(--v4-text) !important;
    font-size:14px !important;
}

[id$="topfinancialdiv"] .cardvalue{
    width:auto !important;
    color:var(--v4-text) !important;
    font-size:20px !important;
    line-height:1.1 !important;
    font-weight:650 !important;
    letter-spacing:-.25px !important;
    text-align:center !important;
}

[id$="topfinancialdiv"] .cardvalue1{
    display:block !important;
    margin-top:8px !important;
    color:var(--v4-muted) !important;
    font-size:10px !important;
    line-height:1.25 !important;
    font-weight:600 !important;
    letter-spacing:.25px !important;
    text-transform:uppercase !important;
    white-space:normal !important;
}

[id$="topfinancialdiv"] .perdentagediv,
[id$="topfinancialdiv"] .recording-symbol{
    display:none !important;
}

.ora-hidden-reservation-section{
    display:none !important;
}

/* Today's Operations from live ASP.NET controls */
.ora-today-operations-title{
    margin:12px 0 7px 0 !important;
}
.ora-live-operations-row{
    display:grid !important;
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
    gap:8px !important;
    margin:0 !important;
}
.ora-live-operations-row > .p-col{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    padding:0 !important;
}
.ora-live-operations-row > .p-col > .card{
    min-height:174px !important;
    height:174px !important;
    margin:0 !important;
    padding:12px !important;
    background:#fff !important;
    border:1px solid var(--v4-line) !important;
    border-radius:0 !important;
    box-shadow:var(--v4-shadow) !important;
}
.ora-live-operations-row .progress-ring{
    width:95px !important;
    height:95px !important;
    margin:0 auto !important;
    position:relative !important;
    background:#fff !important;
}
.ora-live-operations-row .progress-ring svg{
    width:95px !important;
    height:95px !important;
    display:block !important;
}
.ora-live-operations-row .progress-ring circle{
    fill:none !important;
    stroke-width:10 !important;
}
.ora-live-operations-row .progress-ring .bg{
    stroke:#e9edf2 !important;
}
.ora-live-operations-row .progress-ring::before{
    display:none !important;
    content:none !important;
}
.ora-live-operations-row .progress-text{
    color:var(--v4-text) !important;
    font-size:22px !important;
    font-weight:650 !important;
}
.ora-live-operations-row .progress-subtext{
    margin-top:8px !important;
    color:var(--v4-muted) !important;
    font-size:11px !important;
    font-weight:400 !important;
}

/* Prototype lower layout: Bookings + Check-in analysis, then In-House */
.ora-prototype-content-grid{
    display:grid !important;
    grid-template-columns:minmax(0,1.2fr) minmax(340px,.8fr) !important;
    gap:10px !important;
    margin:10px 0 0 0 !important;
}

.ora-prototype-content-grid > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    margin:0 !important;
    padding:0 !important;
}

.ora-prototype-content-grid > [class*="col-"]:not(.ora-bookings-panel):not(.ora-checkin-analysis-panel):not(.ora-inhouse-panel){
    display:none !important;
}

.ora-bookings-panel{
    grid-column:1 !important;
}
.ora-checkin-analysis-panel{
    grid-column:2 !important;
}
.ora-inhouse-panel{
    grid-column:1 / -1 !important;
}

.ora-bookings-panel .card,
.ora-checkin-analysis-panel .card,
.ora-inhouse-panel .dashboard-inhouse-card{
    height:auto !important;
    margin:0 !important;
    background:#fff !important;
    border:1px solid var(--v4-line) !important;
    border-radius:0 !important;
    box-shadow:var(--v4-shadow) !important;
}

.ora-bookings-panel .card-body,
.ora-checkin-analysis-panel .card-body{
    padding:13px !important;
}

.card-titlest,
.dashboard-inhouse-title{
    margin:0 !important;
    color:var(--v4-text) !important;
    font-size:13px !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
}

.card-titlest::before{
    display:none !important;
    content:none !important;
}

.dropdownstyle{
    width:auto !important;
    min-width:86px !important;
    height:36px !important;
    padding:0 9px !important;
    border:1px solid #ccd5df !important;
    background:#fff !important;
    color:var(--v4-text) !important;
    font-size:11px !important;
    font-weight:600 !important;
}

.dashboard-inhouse-titlebar{
    padding:11px 13px !important;
    background:#fbfcfe !important;
    border-bottom:1px solid var(--v4-line) !important;
}
.dashboard-inhouse-count{
    background:#eaf7f2 !important;
    border:1px solid #d3ede5 !important;
    color:var(--v4-green) !important;
}
.dashboard-inhouse-grid th{
    background:#f7f9fc !important;
    color:#59677a !important;
    font-size:10px !important;
    font-weight:600 !important;
    padding:8px 9px !important;
}
.dashboard-inhouse-grid td{
    color:#344054 !important;
    font-size:10.5px !important;
    padding:8px 9px !important;
}

/* Remove oversized empty spacing inherited from old design */
.card-z .card-body{
    min-height:0 !important;
}
hr{
    margin:10px 0 !important;
    border:0 !important;
    border-top:1px solid var(--v4-line) !important;
}

/* Responsive */
@media(max-width:1450px){
    [id$="topfinancialdiv"] > .row{
        grid-template-columns:repeat(6,minmax(0,1fr)) !important;
    }
}
@media(max-width:1100px){
    .ora-prototype-content-grid{
        grid-template-columns:1fr !important;
    }
    .ora-bookings-panel,
    .ora-checkin-analysis-panel,
    .ora-inhouse-panel{
        grid-column:1 !important;
    }
    .ora-live-operations-row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}
@media(max-width:750px){
    [id$="topfinancialdiv"] > .row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
    .ora-live-operations-row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
}
@media(max-width:430px){
    [id$="topfinancialdiv"] > .row,
    .ora-live-operations-row{
        grid-template-columns:1fr !important;
    }
}
</style>


<!-- =========================================================
     FINAL FULL PROTOTYPE DESIGN V5
     Inline-only page control. Existing live controls preserved.
========================================================= -->
<style>
:root{
    --final-bg:#f4f7fb;
    --final-card:#fff;
    --final-text:#172033;
    --final-muted:#6b778c;
    --final-line:#e3e8ef;
    --final-blue:#315b8a;
    --final-green:#118267;
    --final-red:#c84444;
    --final-amber:#ba7418;
    --final-purple:#7556b8;
    --final-shadow:0 5px 18px rgba(23,32,51,.055);
}

body,
#aspnetForm{
    background:var(--final-bg) !important;
    color:var(--final-text) !important;
    font-family:Inter,"Segoe UI",Arial,sans-serif !important;
}

/* =========================================================
   1. TITLE + DATE RANGE — prototype layout
========================================================= */
.ora-dashboard-header-row{
    display:grid !important;
    grid-template-columns:minmax(280px,.8fr) minmax(620px,1.2fr) !important;
    align-items:center !important;
    gap:16px !important;
    margin:0 0 12px 0 !important;
    padding:12px !important;
    background:#fff !important;
    border:1px solid var(--final-line) !important;
    box-shadow:var(--final-shadow) !important;
}

.ora-dashboard-header-row > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    padding:0 !important;
}

.ora-dashboard-title-col{
    align-items:center !important;
}

.ora-dashboard-title-col > div:first-of-type{
    display:none !important;
}

.page-header{
    margin:0 !important;
    padding:0 !important;
    color:var(--final-text) !important;
    font-size:24px !important;
    line-height:1.15 !important;
    font-weight:650 !important;
    letter-spacing:-.3px !important;
    text-transform:none !important;
}

.page-header::after{
    content:"Executive hotel performance, operations and reservation exceptions";
    display:block;
    margin-top:4px;
    color:var(--final-muted);
    font-size:12px;
    line-height:1.3;
    font-weight:400;
    letter-spacing:0;
}

.page-header .tooltip{
    display:none !important;
}

.ora-dashboard-date-col{
    align-items:center !important;
}

.ora-dashboard-date-col > .row{
    display:grid !important;
    grid-template-columns:105px minmax(350px,1fr) 120px !important;
    align-items:end !important;
    gap:8px !important;
    width:100% !important;
    margin:0 !important;
}

.ora-dashboard-date-col > .row > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    padding:0 !important;
}

#ContentPlaceHolder2_lblDateRange{
    display:block !important;
    margin:0 0 6px 0 !important;
    color:#435166 !important;
    font-size:10px !important;
    font-weight:600 !important;
    letter-spacing:.3px !important;
    text-transform:uppercase !important;
    text-align:left !important;
}

#ContentPlaceHolder2_txt_dateRange{
    height:36px !important;
    padding:0 10px !important;
    border:1px solid #ccd5df !important;
    border-radius:0 !important;
    background:#fff !important;
    color:#344054 !important;
    box-shadow:none !important;
    font-size:12px !important;
}

.calendar-icon,
.input-group-addon.calendar-icon{
    width:36px !important;
    height:36px !important;
    border:0 !important;
    background:var(--final-blue) !important;
    color:#fff !important;
    display:flex !important;
    align-items:center !important;
    justify-content:center !important;
}

#ContentPlaceHolder2_btnShow{
    width:100% !important;
    height:36px !important;
    min-width:0 !important;
    margin:0 !important;
    padding:0 14px !important;
    border:1px solid var(--final-blue) !important;
    border-radius:0 !important;
    background:var(--final-blue) !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:11px !important;
    font-weight:600 !important;
    letter-spacing:.3px !important;
}

/* =========================================================
   2. BOOKINGS + CHECK-IN ANALYSIS — prototype proportions
========================================================= */
.ora-prototype-content-grid{
    display:grid !important;
    grid-template-columns:minmax(0,1.2fr) minmax(380px,.8fr) !important;
    gap:10px !important;
    margin:10px 0 0 0 !important;
    align-items:start !important;
}

.ora-prototype-content-grid > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    flex:none !important;
    margin:0 !important;
    padding:0 !important;
}

.ora-prototype-content-grid > [class*="col-"]:not(.ora-bookings-panel):not(.ora-checkin-analysis-panel):not(.ora-inhouse-panel){
    display:none !important;
}

.ora-bookings-panel{
    grid-column:1 !important;
}
.ora-checkin-analysis-panel{
    grid-column:2 !important;
}
.ora-inhouse-panel{
    grid-column:1 / -1 !important;
}

.ora-bookings-panel .card,
.ora-checkin-analysis-panel .card{
    height:420px !important;
    min-height:420px !important;
    margin:0 !important;
    padding:0 !important;
    background:#fff !important;
    border:1px solid var(--final-line) !important;
    border-radius:0 !important;
    box-shadow:var(--final-shadow) !important;
    overflow:hidden !important;
}

.ora-bookings-panel .card-body,
.ora-checkin-analysis-panel .card-body{
    height:100% !important;
    padding:13px !important;
}

.ora-bookings-panel h5,
.ora-checkin-analysis-panel h5{
    min-height:36px !important;
    margin:0 !important;
    padding:0 0 10px 0 !important;
    display:flex !important;
    align-items:center !important;
    justify-content:space-between !important;
    border-bottom:1px solid var(--final-line) !important;
}

.card-titlest{
    color:var(--final-text) !important;
    font-size:13px !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
}

.card-titlest::before{
    display:none !important;
    content:none !important;
}

.dropdownstyle{
    width:86px !important;
    height:36px !important;
    padding:0 9px !important;
    border:1px solid #ccd5df !important;
    border-radius:0 !important;
    background:#fff !important;
    color:var(--final-text) !important;
    font-size:11px !important;
    font-weight:600 !important;
}

.ora-bookings-panel canvas{
    width:100% !important;
    height:330px !important;
    max-height:330px !important;
}

.ora-checkin-analysis-panel .card-body{
    height:420px !important;
}

.ora-checkin-analysis-panel .card-body > div{
    height:340px !important;
}

.ora-checkin-analysis-panel canvas{
    width:100% !important;
    height:330px !important;
    max-height:330px !important;
}

/* =========================================================
   3. CURRENT IN-HOUSE GUESTS — prototype table
========================================================= */
.ora-inhouse-panel{
    margin-top:0 !important;
}

.ora-inhouse-panel .dashboard-inhouse-card{
    margin:0 !important;
    background:#fff !important;
    border:1px solid var(--final-line) !important;
    border-radius:0 !important;
    box-shadow:var(--final-shadow) !important;
    overflow:hidden !important;
}

.dashboard-inhouse-titlebar{
    padding:11px 13px !important;
    background:#fbfcfe !important;
    border-bottom:1px solid var(--final-line) !important;
}

.dashboard-inhouse-title{
    margin:0 !important;
    color:var(--final-text) !important;
    font-size:13px !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
}

.dashboard-inhouse-title::before{
    width:3px !important;
    height:17px !important;
    background:var(--final-blue) !important;
}

.dashboard-inhouse-count{
    height:24px !important;
    padding:0 8px !important;
    background:#eaf7f2 !important;
    border:1px solid #d3ede5 !important;
    color:var(--final-green) !important;
    font-size:10px !important;
    font-weight:600 !important;
}

.dashboard-inhouse-scroll{
    max-height:350px !important;
    overflow:auto !important;
}

.dashboard-inhouse-grid{
    width:100% !important;
    min-width:1150px !important;
    border-collapse:collapse !important;
    border-spacing:0 !important;
}

.dashboard-inhouse-grid th{
    position:sticky !important;
    top:0 !important;
    z-index:2 !important;
    padding:8px 9px !important;
    background:#f7f9fc !important;
    color:#59677a !important;
    border:0 !important;
    border-right:1px solid var(--final-line) !important;
    border-bottom:1px solid var(--final-line) !important;
    font-size:10px !important;
    font-weight:600 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
}

.dashboard-inhouse-grid td{
    padding:8px 9px !important;
    background:#fff !important;
    color:#344054 !important;
    border:0 !important;
    border-right:1px solid var(--final-line) !important;
    border-bottom:1px solid var(--final-line) !important;
    font-size:10.5px !important;
    font-weight:400 !important;
}

.dashboard-inhouse-grid tr:hover td{
    background:#fbfcfe !important;
}

.dashboard-room-badge{
    padding:3px 7px !important;
    background:#edf4fb !important;
    border:1px solid #dce8f5 !important;
    color:var(--final-blue) !important;
}

.btn-actions{
    height:28px !important;
    min-width:82px !important;
    padding:0 10px !important;
    border:1px solid var(--final-blue) !important;
    border-radius:0 !important;
    background:var(--final-blue) !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:10px !important;
    line-height:26px !important;
}

/* =========================================================
   4. REMOVE LEGACY DATA AFTER IN-HOUSE
   Only obsolete purchasing/rate/status/report popups are hidden.
   Cancellation and No Show popups remain available.
========================================================= */
#ContentPlaceHolder2_popupex,
#ContentPlaceHolder2_changeratepopup,
#ContentPlaceHolder2_changestatuspopup,
#ContentPlaceHolder2_Purchasingpopupshow,
#ContentPlaceHolder2_Paymentpopshow,
#ContentPlaceHolder2_PAYABLEAMOUNTPOPUP{
    display:none !important;
}





/* Keep legitimate modal popups fixed instead of rendering under page */
#ContentPlaceHolder2_NOshowpopup,
#ContentPlaceHolder2_Cancellationpopup,
#ContentPlaceHolder2_CheckInpopup,
#ContentPlaceHolder2_Pendingspopup,
#ContentPlaceHolder2_Pendingpaypopup,
#ContentPlaceHolder2_roomsecuritypopshow,
#ContentPlaceHolder2_refundpopup,
#ContentPlaceHolder2_Expenseshowpopup,
#ContentPlaceHolder2_totalcustomerpopup,
#ContentPlaceHolder2_profitlosspopup,
#ContentPlaceHolder2_Staffpopup{
    position:fixed !important;
}

/* Responsive */
@media(max-width:1100px){
    .ora-dashboard-header-row{
        grid-template-columns:1fr !important;
    }
    .ora-dashboard-date-col > .row{
        grid-template-columns:105px 1fr 120px !important;
    }
    .ora-prototype-content-grid{
        grid-template-columns:1fr !important;
    }
    .ora-bookings-panel,
    .ora-checkin-analysis-panel,
    .ora-inhouse-panel{
        grid-column:1 !important;
    }
}

@media(max-width:700px){
    .ora-dashboard-date-col > .row{
        grid-template-columns:1fr !important;
    }
    #ContentPlaceHolder2_lblDateRange{
        margin-bottom:0 !important;
    }
}
</style>


<!-- DASHBOARD BUILD: 2026-08-05-ACTUAL-MARKUP-PROTOTYPE-V6 -->
<style>
.ora-v6-header .input-group{
    display:flex !important;
    width:100% !important;
    margin:0 !important;
}
.ora-v6-header .form-control{
    height:36px !important;
    border:1px solid #ccd5df !important;
    border-radius:0 !important;
    background:#fff !important;
    box-shadow:none !important;
    color:#344054 !important;
    font-size:12px !important;
}
.ora-v6-header .calendar-icon{
    width:36px !important;
    height:36px !important;
    min-width:36px !important;
    display:flex !important;
    align-items:center !important;
    justify-content:center !important;
    border:0 !important;
    background:#315b8a !important;
    color:#fff !important;
}
.ora-v6-header #ContentPlaceHolder2_btnShow{
    width:100% !important;
    height:36px !important;
    margin:0 !important;
    padding:0 !important;
    border:1px solid #315b8a !important;
    border-radius:0 !important;
    background:#315b8a !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:11px !important;
    font-weight:600 !important;
}

.ora-v6-content-grid{
    display:grid !important;
    grid-template-columns:minmax(0,1.2fr) minmax(360px,.8fr) !important;
    gap:10px !important;
    margin-top:10px !important;
}
.ora-v6-bookings,
.ora-v6-checkin{
    min-height:430px !important;
}
.ora-v6-inhouse-wrap{
    grid-column:1 / -1 !important;
}

.ora-v6-time-grid{
    display:grid;
    grid-template-columns:repeat(3,minmax(0,1fr));
    gap:7px;
    padding:14px;
}
.ora-v6-time-slot{
    min-height:67px;
    padding:10px 7px;
    background:#f5f8fb;
    border:1px solid #e3e8ef;
    text-align:center;
}
.ora-v6-time-slot strong{
    display:block;
    color:#172033;
    font-size:18px;
    font-weight:650;
}
.ora-v6-time-slot span{
    display:block;
    margin-top:4px;
    color:#6b778c;
    font-size:9px;
}
.ora-v6-time-metrics{
    padding:0 14px 14px;
}
.ora-v6-time-metric{
    display:flex;
    justify-content:space-between;
    gap:14px;
    padding:9px 0;
    border-bottom:1px solid #e3e8ef;
}
.ora-v6-time-metric:last-child{
    border-bottom:0;
}
.ora-v6-time-metric span{
    color:#6b778c;
    font-size:11px;
}
.ora-v6-time-metric strong{
    color:#172033;
    font-size:11px;
    font-weight:650;
}

.ora-v6-inhouse-wrap .ora-inhouse-panel{
    width:100% !important;
    max-width:none !important;
    margin:0 !important;
    padding:0 !important;
}
.ora-v6-inhouse-wrap .dashboard-inhouse-card{
    margin:0 !important;
    border:1px solid #e3e8ef !important;
    border-radius:0 !important;
    box-shadow:0 5px 18px rgba(23,32,51,.055) !important;
    background:#fff !important;
}
.ora-v6-inhouse-wrap .dashboard-inhouse-titlebar{
    padding:11px 13px !important;
    background:#fbfcfe !important;
    border-bottom:1px solid #e3e8ef !important;
}
.ora-v6-inhouse-wrap .dashboard-inhouse-grid{
    min-width:1150px !important;
    border-collapse:collapse !important;
}
.ora-v6-inhouse-wrap .dashboard-inhouse-grid th{
    padding:8px 9px !important;
    background:#f7f9fc !important;
    color:#59677a !important;
    border:0 !important;
    border-right:1px solid #e3e8ef !important;
    border-bottom:1px solid #e3e8ef !important;
    font-size:10px !important;
    font-weight:600 !important;
    text-transform:none !important;
}
.ora-v6-inhouse-wrap .dashboard-inhouse-grid td{
    padding:8px 9px !important;
    background:#fff !important;
    color:#344054 !important;
    border:0 !important;
    border-right:1px solid #e3e8ef !important;
    border-bottom:1px solid #e3e8ef !important;
    font-size:10.5px !important;
}
.ora-v6-status-badge{
    display:inline-flex;
    padding:3px 7px;
    background:#eaf7f2;
    border:1px solid #d3ede5;
    color:#118267;
    font-size:9px;
    font-weight:650;
    text-transform:capitalize;
}
.ora-v6-inhouse-wrap .btn-actions{
    height:28px !important;
    min-width:82px !important;
    padding:0 10px !important;
    border:1px solid #315b8a !important;
    border-radius:0 !important;
    background:#315b8a !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:10px !important;
    line-height:26px !important;
}

/* Legacy page sections after the new dashboard markup are physically removed. */
@media(max-width:1000px){
    .ora-v6-header{
        flex-direction:column !important;
        align-items:stretch !important;
    }
    .ora-v6-datebar{
        min-width:0 !important;
    }
    .ora-v6-content-grid{
        grid-template-columns:1fr !important;
    }
    .ora-v6-inhouse-wrap{
        grid-column:1 !important;
    }
}
@media(max-width:650px){
    .ora-v6-datebar{
        flex-direction:column !important;
        align-items:stretch !important;
    }
    .ora-v6-datebar > div{
        width:100% !important;
        min-width:0 !important;
    }
    .ora-v6-time-grid{
        grid-template-columns:repeat(2,minmax(0,1fr));
    }
}
</style>


<!-- =========================================================
     POPUP / ACTION / SECURITY FIX V8
========================================================= -->
<style>
/* Remove Security Deposit from dashboard without removing its server control. */
#ContentPlaceHolder2_topfinancialdiv > .row > div:nth-child(3){
    display:none !important;
}

/* Financial grid now contains six visible cards. */
#ContentPlaceHolder2_topfinancialdiv > .row{
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
}

/* Compatibility controls and legacy non-required popups must never appear. */
#oraLegacyCompatibilityControls{
    display:none !important;
}

#ContentPlaceHolder2_popupex,
#ContentPlaceHolder2_changeratepopup,
#ContentPlaceHolder2_changestatuspopup,
#ContentPlaceHolder2_Availablepopup,
#ContentPlaceHolder2_Occupypopup,
#ContentPlaceHolder2_Dirtyrooompopup,
#ContentPlaceHolder2_Purchasingpopupshow,
#ContentPlaceHolder2_Paymentpopshow,
#ContentPlaceHolder2_PAYABLEAMOUNTPOPUP{
    display:none !important;
}

/* Popup update panel must remain available for legitimate modal popups. */
#ContentPlaceHolder2_popupUpdatePanel{
    display:block !important;
    position:static !important;
    width:auto !important;
    height:auto !important;
    overflow:visible !important;
}

/* Legitimate popups remain hidden until their server event opens them. */
#ContentPlaceHolder2_NOshowpopup,
#ContentPlaceHolder2_Cancellationpopup,
#ContentPlaceHolder2_CheckInpopup,
#ContentPlaceHolder2_Pendingspopup,
#ContentPlaceHolder2_Pendingpaypopup,
#ContentPlaceHolder2_Revenuepopup,
#ContentPlaceHolder2_Expenseshowpopup,
#ContentPlaceHolder2_roomsecuritypopshow,
#ContentPlaceHolder2_totalcustomerpopup,
#ContentPlaceHolder2_profitlosspopup,
#ContentPlaceHolder2_Staffpopup,
#ContentPlaceHolder2_refundpopup{
    position:fixed !important;
    top:50% !important;
    left:50% !important;
    transform:translate(-50%,-50%) !important;
    z-index:10001 !important;
}

/* In-house action dropdown */
.dashboard-action-wrap{
    position:relative !important;
    display:inline-block !important;
}
.dashboard-action-menu{
    display:none !important;
    position:fixed !important;
    min-width:220px !important;
    padding:8px !important;
    background:#fff !important;
    border:1px solid #e3e8ef !important;
    border-radius:0 !important;
    box-shadow:0 16px 38px rgba(23,32,51,.18) !important;
    z-index:10020 !important;
}
.dashboard-action-wrap.open .dashboard-action-menu{
    display:block !important;
}
.dashboard-action-menu .action-item{
    display:flex !important;
    align-items:center !important;
    gap:9px !important;
    padding:9px 10px !important;
    color:#344054 !important;
    background:#fff !important;
    border-radius:0 !important;
    text-decoration:none !important;
    font-size:11px !important;
}
.dashboard-action-menu .action-item:hover{
    background:#f5f8fb !important;
    color:#172033 !important;
    transform:none !important;
}

@media(max-width:1250px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}
@media(max-width:700px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
}
</style>

<script type="text/javascript">
    (function () {
        function closeAllDashboardMenus(exceptWrap) {
            var wraps = document.querySelectorAll('.dashboard-action-wrap.open');

            for (var i = 0; i < wraps.length; i++) {
                if (exceptWrap && wraps[i] === exceptWrap) continue;

                wraps[i].classList.remove('open');

                var oldMenu = wraps[i].querySelector('.dashboard-action-menu');
                if (oldMenu) {
                    oldMenu.style.top = '';
                    oldMenu.style.left = '';
                    oldMenu.style.right = '';
                }
            }
        }

        window.DashboardCloseAllActionMenus = function () {
            closeAllDashboardMenus(null);
        };

        window.DashboardToggleActionMenu = function (button, eventObject) {
            if (eventObject) {
                eventObject.preventDefault();
                eventObject.stopPropagation();
            }

            if (!button) return false;

            var wrap = button.closest
                ? button.closest('.dashboard-action-wrap')
                : button.parentNode;

            if (!wrap) return false;

            var menu = wrap.querySelector('.dashboard-action-menu');
            if (!menu) return false;

            var wasOpen = wrap.classList.contains('open');
            closeAllDashboardMenus(wrap);

            if (wasOpen) {
                wrap.classList.remove('open');
                return false;
            }

            var rect = button.getBoundingClientRect();
            var menuWidth = 220;
            var left = rect.right - menuWidth;

            if (left < 8) left = 8;
            if (left + menuWidth > window.innerWidth - 8) {
                left = window.innerWidth - menuWidth - 8;
            }

            var top = rect.bottom + 5;

            wrap.classList.add('open');
            menu.style.left = left + 'px';
            menu.style.top = top + 'px';
            menu.style.right = 'auto';

            return false;
        };

        function bindGlobalDashboardActions() {
            if (document.documentElement.getAttribute('data-dashboard-action-bound') === '1') {
                return;
            }

            document.documentElement.setAttribute('data-dashboard-action-bound', '1');

            document.addEventListener('click', function (eventObject) {
                var target = eventObject.target;

                if (!target || !target.closest ||
                    !target.closest('.dashboard-action-wrap')) {
                    closeAllDashboardMenus(null);
                }
            });

            document.addEventListener('keydown', function (eventObject) {
                if (eventObject.key === 'Escape') {
                    closeAllDashboardMenus(null);
                }
            });

            window.addEventListener('scroll', function () {
                closeAllDashboardMenus(null);
            }, true);

            window.addEventListener('resize', function () {
                closeAllDashboardMenus(null);
            });
        }

        bindGlobalDashboardActions();

        if (window.Sys && Sys.WebForms &&
            Sys.WebForms.PageRequestManager) {
            Sys.WebForms.PageRequestManager.getInstance()
                .add_endRequest(function () {
                    closeAllDashboardMenus(null);
                });
        }
    })();
</script>


<!-- =========================================================
     COMPACT CARDS / DETAIL POPUPS / ACTION MENU V9
========================================================= -->
<style>
/* Six visible financial cards after Security Deposit removal */
#ContentPlaceHolder2_topfinancialdiv > .row{
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
}

/* Smaller, clean dashboard blocks */
#ContentPlaceHolder2_topfinancialdiv .cardst{
    height:92px !important;
    min-height:92px !important;
    padding:10px 11px !important;
}
#ContentPlaceHolder2_topfinancialdiv .cardstimg{
    width:28px !important;
    height:28px !important;
    padding:6px !important;
}
#ContentPlaceHolder2_topfinancialdiv .cardvalue{
    font-size:18px !important;
}
#ContentPlaceHolder2_topfinancialdiv .cardvalue1{
    margin-top:6px !important;
    font-size:9px !important;
}

/* No movement / scaling on hover anywhere */
.card-hover:hover,
.cardst:hover,
.kpi:hover,
.clickable:hover,
.ora-detail-card:hover,
.ora-live-operations-row > .p-col > .card:hover{
    transform:none !important;
    scale:1 !important;
    box-shadow:0 5px 18px rgba(23,32,51,.055) !important;
}
.card-hover,
.cardst,
.kpi,
.clickable,
.ora-detail-card{
    transition:none !important;
}

/* Compact Today's Operations */
.ora-live-operations-row > .p-col > .card{
    height:150px !important;
    min-height:150px !important;
    padding:10px !important;
}
.ora-live-operations-row .progress-ring{
    width:82px !important;
    height:82px !important;
}
.ora-live-operations-row .progress-ring svg{
    width:82px !important;
    height:82px !important;
}
.ora-live-operations-row .progress-text{
    font-size:19px !important;
}
.ora-live-operations-row .progress-subtext{
    margin-top:5px !important;
    font-size:10px !important;
}
.ora-detail-card{
    cursor:pointer;
}

/* All detail popups use the same small professional modal design */
.ora-compact-detail-popup{
    display:none;
    position:fixed !important;
    top:50% !important;
    left:50% !important;
    transform:translate(-50%,-50%) !important;
    width:min(1040px,92vw) !important;
    height:auto !important;
    max-height:74vh !important;
    padding:0 !important;
    background:#fff !important;
    border:1px solid #dfe5ec !important;
    border-radius:0 !important;
    box-shadow:0 22px 65px rgba(15,23,42,.24) !important;
    overflow:auto !important;
    z-index:10050 !important;
}
.ora-compact-detail-popup::before{
    display:none !important;
    content:none !important;
}
.ora-compact-detail-popup > div:first-child,
.ora-compact-detail-popup h3,
.ora-compact-detail-popup h4{
    background:#fbfcfe !important;
    color:#172033 !important;
    border-bottom:1px solid #e3e8ef !important;
}
.ora-compact-detail-popup table{
    width:100% !important;
    background:#fff !important;
    border-collapse:collapse !important;
}
.ora-compact-detail-popup table th{
    padding:8px 9px !important;
    background:#f7f9fc !important;
    color:#475467 !important;
    border:1px solid #e3e8ef !important;
    font-size:10px !important;
    font-weight:650 !important;
    text-transform:none !important;
    letter-spacing:0 !important;
}
.ora-compact-detail-popup table td{
    padding:8px 9px !important;
    background:#fff !important;
    color:#344054 !important;
    border:1px solid #e3e8ef !important;
    font-size:10.5px !important;
}
.ora-compact-detail-popup input,
.ora-compact-detail-popup select{
    height:34px !important;
    border:1px solid #ccd5df !important;
    background:#fff !important;
    color:#344054 !important;
    font-size:11px !important;
}

/* Ensure No Show is visible exactly like Cancellation after server click */
#ContentPlaceHolder2_NOshowpopup[style*="display: block"],
#ContentPlaceHolder2_Cancellationpopup[style*="display: block"]{
    display:block !important;
}

/* Enhanced compact action dropdown */
.dashboard-action-menu{
    min-width:205px !important;
    padding:6px !important;
    border:1px solid #dfe5ec !important;
    border-radius:8px !important;
    box-shadow:0 16px 38px rgba(23,32,51,.18) !important;
}
.dashboard-action-menu::before{
    display:none !important;
    content:none !important;
}
.dashboard-action-menu .action-item{
    min-height:34px !important;
    padding:8px 9px !important;
    border-radius:6px !important;
    color:#344054 !important;
    font-size:10.5px !important;
    font-weight:500 !important;
}
.dashboard-action-menu .action-item i{
    width:17px !important;
    height:17px !important;
    font-size:13px !important;
}
.dashboard-action-menu .action-item:hover{
    background:#f1f5f9 !important;
    color:#172033 !important;
    transform:none !important;
}
.btn-actions{
    height:28px !important;
    min-width:88px !important;
    padding:0 9px !important;
    border-radius:6px !important;
    box-shadow:none !important;
    font-size:10px !important;
    line-height:26px !important;
}
.btn-actions:hover{
    transform:none !important;
    box-shadow:none !important;
}

@media(max-width:1250px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}
@media(max-width:700px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
}
</style>


<!-- =========================================================
     UNIFIED HTML-STYLE DASHBOARD POPUPS V10
========================================================= -->
<style>
/* Fast lightweight overlay: no expensive blur filter. */
#ContentPlaceHolder2_overlay,
.screenblur{
    background:rgba(15,23,42,.34) !important;
    backdrop-filter:none !important;
    -webkit-backdrop-filter:none !important;
    transition:none !important;
    animation:none !important;
}

/* Every dashboard detail popup uses one professional design. */
.ora-compact-detail-popup,
#ContentPlaceHolder2_NOshowpopup,
#ContentPlaceHolder2_Cancellationpopup,
#ContentPlaceHolder2_CheckInpopup,
#ContentPlaceHolder2_Pendingspopup,
#ContentPlaceHolder2_Revenuepopup,
#ContentPlaceHolder2_Pendingpaypopup,
#ContentPlaceHolder2_Expenseshowpopup,
#ContentPlaceHolder2_roomsecuritypopshow,
#ContentPlaceHolder2_totalcustomerpopup,
#ContentPlaceHolder2_profitlosspopup,
#ContentPlaceHolder2_Staffpopup,
#ContentPlaceHolder2_refundpopup,
#ContentPlaceHolder2_Availablepopup,
#ContentPlaceHolder2_Occupypopup,
#ContentPlaceHolder2_Dirtyrooompopup{
    position:fixed !important;
    top:50% !important;
    left:50% !important;
    transform:translate(-50%,-50%) !important;
    width:min(1100px,92vw) !important;
    height:auto !important;
    max-height:76vh !important;
    margin:0 !important;
    padding:0 !important;
    background:#fff !important;
    border:1px solid #dfe5ec !important;
    border-radius:0 !important;
    box-shadow:0 24px 70px rgba(15,23,42,.22) !important;
    overflow:hidden !important;
    z-index:10050 !important;
    transition:none !important;
    animation:none !important;
    font-family:Inter,"Segoe UI",Arial,sans-serif !important;
}

/* Remove old coloured strip and inherited orange header. */
.ora-compact-detail-popup::before,
#ContentPlaceHolder2_NOshowpopup::before,
#ContentPlaceHolder2_Cancellationpopup::before{
    display:none !important;
    content:none !important;
}

.ora-compact-detail-popup > div,
#ContentPlaceHolder2_NOshowpopup > div,
#ContentPlaceHolder2_Cancellationpopup > div{
    margin:0 !important;
    padding:0 !important;
    background:#fff !important;
}

/* Header exactly like the HTML prototype. */
.ora-compact-detail-popup .popupheaderforpopup,
#ContentPlaceHolder2_NOshowpopup .popupheaderforpopup,
#ContentPlaceHolder2_Cancellationpopup .popupheaderforpopup{
    display:flex !important;
    align-items:center !important;
    justify-content:space-between !important;
    min-height:58px !important;
    margin:0 !important;
    padding:11px 14px !important;
    background:#fbfcfe !important;
    border:0 !important;
    border-bottom:1px solid #e3e8ef !important;
    box-shadow:none !important;
    color:#172033 !important;
}

.ora-compact-detail-popup .headertext,
#ContentPlaceHolder2_NOshowpopup .headertext,
#ContentPlaceHolder2_Cancellationpopup .headertext{
    color:#172033 !important;
    font-size:14px !important;
    line-height:1.2 !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
    text-transform:none !important;
    font-family:Inter,"Segoe UI",Arial,sans-serif !important;
}

/* Small clean close button. */
.ora-compact-detail-popup .popupheaderforpopup input[type="image"],
#ContentPlaceHolder2_NOshowpopup .popupheaderforpopup input[type="image"],
#ContentPlaceHolder2_Cancellationpopup .popupheaderforpopup input[type="image"]{
    width:30px !important;
    height:30px !important;
    min-width:30px !important;
    margin:0 !important;
    padding:8px !important;
    border:0 !important;
    border-radius:0 !important;
    background:#eef2f6 !important;
    box-shadow:none !important;
    object-fit:contain !important;
    opacity:.72 !important;
    cursor:pointer !important;
}
.ora-compact-detail-popup .popupheaderforpopup input[type="image"]:hover{
    background:#e5eaf0 !important;
    opacity:1 !important;
    transform:none !important;
}

/* Scroll only inside modal content. */
.ora-compact-detail-popup .repeaterheight,
.ora-compact-detail-popup .repeaterheight2,
.ora-compact-detail-popup .repeaterheightforproftloss,
#ContentPlaceHolder2_NOshowpopup .repeaterheight,
#ContentPlaceHolder2_Cancellationpopup .repeaterheight{
    height:auto !important;
    max-height:calc(76vh - 59px) !important;
    margin:0 !important;
    padding:0 !important;
    overflow:auto !important;
    background:#fff !important;
}

/* Professional table used by every popup. */
.ora-compact-detail-popup table,
#ContentPlaceHolder2_NOshowpopup table,
#ContentPlaceHolder2_Cancellationpopup table{
    width:100% !important;
    min-width:780px !important;
    margin:0 !important;
    border:0 !important;
    border-collapse:collapse !important;
    border-spacing:0 !important;
    background:#fff !important;
}

.ora-compact-detail-popup table thead,
#ContentPlaceHolder2_NOshowpopup table thead,
#ContentPlaceHolder2_Cancellationpopup table thead{
    position:sticky !important;
    top:0 !important;
    z-index:2 !important;
}

.ora-compact-detail-popup table th,
#ContentPlaceHolder2_NOshowpopup table th,
#ContentPlaceHolder2_Cancellationpopup table th{
    padding:9px 10px !important;
    background:#f7f9fc !important;
    color:#475467 !important;
    border:0 !important;
    border-right:1px solid #e3e8ef !important;
    border-bottom:1px solid #e3e8ef !important;
    font-size:10px !important;
    line-height:1.25 !important;
    font-weight:650 !important;
    letter-spacing:0 !important;
    text-align:left !important;
    text-transform:none !important;
    white-space:nowrap !important;
}

.ora-compact-detail-popup table td,
#ContentPlaceHolder2_NOshowpopup table td,
#ContentPlaceHolder2_Cancellationpopup table td{
    padding:9px 10px !important;
    background:#fff !important;
    color:#344054 !important;
    border:0 !important;
    border-right:1px solid #e3e8ef !important;
    border-bottom:1px solid #e3e8ef !important;
    font-size:10.5px !important;
    line-height:1.35 !important;
    font-weight:400 !important;
    text-align:left !important;
    white-space:nowrap !important;
}

.ora-compact-detail-popup table tbody tr:nth-child(even) td,
#ContentPlaceHolder2_NOshowpopup table tbody tr:nth-child(even) td,
#ContentPlaceHolder2_Cancellationpopup table tbody tr:nth-child(even) td{
    background:#fbfcfe !important;
}

.ora-compact-detail-popup table tbody tr:hover td,
#ContentPlaceHolder2_NOshowpopup table tbody tr:hover td,
#ContentPlaceHolder2_Cancellationpopup table tbody tr:hover td{
    background:#f5f8fb !important;
}

/* Inputs and buttons inside operational popups. */
.ora-compact-detail-popup input[type="text"],
.ora-compact-detail-popup input[type="date"],
.ora-compact-detail-popup select,
.ora-compact-detail-popup textarea{
    min-height:34px !important;
    padding:6px 9px !important;
    border:1px solid #ccd5df !important;
    border-radius:0 !important;
    background:#fff !important;
    color:#344054 !important;
    box-shadow:none !important;
    font-size:11px !important;
}

.ora-compact-detail-popup input[type="submit"],
.ora-compact-detail-popup button{
    min-height:34px !important;
    padding:0 12px !important;
    border:1px solid #315b8a !important;
    border-radius:0 !important;
    background:#315b8a !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:10.5px !important;
    font-weight:600 !important;
}
.ora-compact-detail-popup input[type="submit"]:hover,
.ora-compact-detail-popup button:hover{
    transform:none !important;
    background:#274c76 !important;
    box-shadow:none !important;
}

/* No Show and Cancellation visible state must override legacy styles. */
#ContentPlaceHolder2_NOshowpopup[style*="display: block"],
#ContentPlaceHolder2_Cancellationpopup[style*="display: block"]{
    display:block !important;
}

/* Responsive popup width. */
@media(max-width:700px){
    .ora-compact-detail-popup,
    #ContentPlaceHolder2_NOshowpopup,
    #ContentPlaceHolder2_Cancellationpopup{
        width:96vw !important;
        max-height:84vh !important;
    }
    .ora-compact-detail-popup .repeaterheight,
    #ContentPlaceHolder2_NOshowpopup .repeaterheight,
    #ContentPlaceHolder2_Cancellationpopup .repeaterheight{
        max-height:calc(84vh - 59px) !important;
    }
}
</style>


<!-- =========================================================
     CANCELLATION FULL DETAILS + COMPLETE RINGS V11
========================================================= -->
<style>
/* Security Deposit is removed even if card order changes. */
#ContentPlaceHolder2_topfinancialdiv .ora-security-deposit-card{
    display:none !important;
}

/* Complete circular progress rings: prevent SVG clipping. */
.ora-live-operations-row .progress-ring{
    overflow:visible !important;
}
.ora-live-operations-row .progress-ring svg{
    display:block !important;
    overflow:visible !important;
    margin:0 auto !important;
}
.ora-live-operations-row .progress-ring svg circle{
    vector-effect:non-scaling-stroke;
}
.ora-live-operations-row .progress-ring .progress{
    transform-origin:60px 60px !important;
}

/* Full cancellation popup is intentionally wider because it has 14 columns. */
.ora-cancellation-ledger-popup{
    width:min(1450px,96vw) !important;
    max-height:82vh !important;
}
.ora-cancellation-ledger-popup .popupheaderforpopup{
    min-height:58px !important;
}
.ora-popup-date-range{
    margin-top:3px;
    color:#6b778c;
    font-size:9px;
    font-weight:400;
}
.ora-cancellation-table-wrap{
    max-height:calc(82vh - 59px) !important;
    overflow:auto !important;
}
.ora-cancellation-ledger-table{
    min-width:1450px !important;
    table-layout:auto !important;
}
.ora-cancellation-ledger-table th,
.ora-cancellation-ledger-table td{
    white-space:nowrap !important;
}
.ora-cancellation-ledger-table .text-right{
    text-align:right !important;
}
.ora-cancellation-ledger-table .text-center{
    text-align:center !important;
}
.ora-room-number{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    min-width:34px;
    padding:3px 6px;
    background:#edf4fb;
    border:1px solid #dce8f5;
    color:#315b8a;
    font-size:10px;
}
.ora-cancellation-note-row td{
    padding:7px 9px !important;
    background:#fff8ee !important;
    color:#344054 !important;
    font-size:10.5px !important;
    white-space:normal !important;
}
.ora-cancellation-note-row td span{
    margin-right:5px;
    color:#a33d0c;
    font-weight:700;
}
.ora-cancellation-action{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    width:36px;
    height:30px;
    background:#f5f7fa;
    border:1px solid #dfe5ec;
    border-radius:7px;
    color:#315b8a !important;
    font-size:14px;
    text-decoration:none !important;
}
.ora-cancellation-action:hover{
    background:#edf4fb;
    transform:none !important;
}
.ora-popup-empty{
    padding:24px;
    color:#6b778c;
    font-size:11px;
    text-align:center;
}
</style>


<!-- =========================================================
     FINAL LAYOUT / SINGLE-ROW CANCELLATION / COMPLETE RINGS V12
========================================================= -->
<style>
/* Removed cards never leave blank space. */
#ContentPlaceHolder2_topfinancialdiv > .row{
    display:grid !important;
    grid-template-columns:repeat(6,minmax(0,1fr)) !important;
    grid-auto-flow:row dense !important;
    gap:8px !important;
    align-items:stretch !important;
}
#ContentPlaceHolder2_topfinancialdiv > .row > [class*="col-"]{
    width:auto !important;
    max-width:none !important;
    min-width:0 !important;
    flex:none !important;
    margin:0 !important;
    padding:0 !important;
}
#ContentPlaceHolder2_topfinancialdiv .ora-security-card{
    display:none !important;
}
#ContentPlaceHolder2_topfinancialdiv .cardst{
    width:100% !important;
    height:92px !important;
    min-height:92px !important;
}

/* Exactly one cancellation/customer record per row. */
.ora-cancellation-ledger-table{
    min-width:1540px !important;
}
.ora-cancellation-main-row td{
    vertical-align:middle !important;
    white-space:nowrap !important;
}
.ora-cancellation-notes-cell{
    min-width:280px !important;
    max-width:360px !important;
    white-space:nowrap !important;
    overflow:hidden !important;
    text-overflow:ellipsis !important;
}
.ora-cancellation-note-row,
.ora-cancellation-action{
    display:none !important;
}

/* Fully visible circular progress charts. */
.ora-live-operations-row > .p-col > .card{
    height:164px !important;
    min-height:164px !important;
    padding:10px !important;
    overflow:visible !important;
}
.ora-live-operations-row .progress-ring{
    position:relative !important;
    width:104px !important;
    height:104px !important;
    margin:0 auto !important;
    overflow:visible !important;
}
.ora-live-operations-row .progress-ring svg{
    display:block !important;
    width:104px !important;
    height:104px !important;
    margin:0 auto !important;
    overflow:visible !important;
    transform:rotate(-90deg) !important;
}
.ora-live-operations-row .progress-ring svg circle{
    cx:60 !important;
    cy:60 !important;
    r:48 !important;
    fill:none !important;
    stroke-width:10 !important;
    stroke-linecap:round !important;
}
.ora-live-operations-row .progress-ring .progress{
    transform-origin:60px 60px !important;
    filter:none !important;
}
.ora-live-operations-row .progress-text{
    top:50% !important;
    left:50% !important;
    transform:translate(-50%,-50%) !important;
    font-size:20px !important;
}
.ora-live-operations-row .progress-subtext{
    margin-top:6px !important;
    font-size:10px !important;
}

/* No movement on hover. */
#ContentPlaceHolder2_topfinancialdiv .cardst:hover,
.ora-live-operations-row > .p-col > .card:hover{
    transform:none !important;
    box-shadow:0 5px 18px rgba(23,32,51,.055) !important;
}

@media(max-width:1250px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(3,minmax(0,1fr)) !important;
    }
}
@media(max-width:700px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:repeat(2,minmax(0,1fr)) !important;
    }
}
@media(max-width:430px){
    #ContentPlaceHolder2_topfinancialdiv > .row{
        grid-template-columns:1fr !important;
    }
}
</style>


<!-- =========================================================
     CURRENT IN-HOUSE REQUIRED COLUMNS V13
========================================================= -->
<style>
.ora-inhouse-required-columns{
    min-width:1320px !important;
    table-layout:auto !important;
}

.ora-inhouse-required-columns th,
.ora-inhouse-required-columns td{
    vertical-align:middle !important;
    white-space:nowrap !important;
}

.ora-inhouse-required-columns th{
    padding:9px 10px !important;
}

.ora-inhouse-required-columns td{
    padding:9px 10px !important;
}

.ora-inhouse-serial{
    text-align:center !important;
    color:#6b778c !important;
    font-weight:600 !important;
}

.ora-ota-booking-number{
    color:#315b8a !important;
    font-weight:600 !important;
}

.ora-stay-dates{
    min-width:205px !important;
}

.ora-date-arrow{
    display:inline-block;
    margin:0 7px;
    color:#98a2b3;
    font-weight:700;
}

.ora-inhouse-stays{
    text-align:center !important;
    color:#172033 !important;
    font-weight:650 !important;
}

.ora-inhouse-stays span{
    display:block;
    margin-top:2px;
    color:#98a2b3;
    font-size:8.5px;
    font-weight:400;
}

.ora-booking-platform{
    display:inline-flex;
    align-items:center;
    min-height:24px;
    padding:3px 7px;
    background:#f5f8fb;
    border:1px solid #e3e8ef;
    color:#344054;
    font-size:9.5px;
    font-weight:600;
}

.ora-inhouse-required-columns tbody tr:hover td{
    background:#fbfcfe !important;
    transform:none !important;
}
</style>


<!-- =========================================================
     CURRENT IN-HOUSE ACTION V14
========================================================= -->
<style>
.ora-inhouse-required-columns{
    min-width:1180px !important;
}

.ora-inhouse-required-columns th:last-child,
.ora-inhouse-required-columns td:last-child{
    text-align:center !important;
}

.dashboard-action-col{
    overflow:visible !important;
}

.dashboard-action-wrap{
    position:relative !important;
    display:inline-block !important;
}

.btn-actions{
    display:inline-flex !important;
    align-items:center !important;
    justify-content:center !important;
    gap:6px !important;
    min-width:88px !important;
}

.dashboard-action-menu{
    text-align:left !important;
}
</style>


<!-- =========================================================
     RESTORED WORKING IN-HOUSE ACTION MENU V15
========================================================= -->
<style>
.dashboard-action-wrap{
    position:relative !important;
    display:inline-block !important;
}

.dashboard-action-menu{
    display:none !important;
    position:fixed !important;
    width:220px !important;
    min-width:220px !important;
    margin:0 !important;
    padding:6px !important;
    list-style:none !important;
    background:#fff !important;
    border:1px solid #dfe5ec !important;
    border-radius:8px !important;
    box-shadow:0 16px 38px rgba(23,32,51,.18) !important;
    z-index:10050 !important;
}

.dashboard-action-wrap.open .dashboard-action-menu{
    display:block !important;
}

.dashboard-action-menu li{
    display:block !important;
    margin:0 !important;
    padding:0 !important;
}

.dashboard-action-menu .action-item{
    display:flex !important;
    align-items:center !important;
    gap:9px !important;
    width:100% !important;
    min-height:36px !important;
    padding:8px 9px !important;
    color:#344054 !important;
    background:#fff !important;
    border:0 !important;
    border-radius:6px !important;
    text-decoration:none !important;
    font-size:10.5px !important;
    font-weight:500 !important;
    line-height:1.2 !important;
    white-space:nowrap !important;
}

.dashboard-action-menu .action-item:hover{
    color:#172033 !important;
    background:#f1f5f9 !important;
    transform:none !important;
}

.dashboard-action-menu .action-item i{
    width:17px !important;
    min-width:17px !important;
    color:#315b8a !important;
    font-size:13px !important;
    text-align:center !important;
}

.btn-actions{
    display:inline-flex !important;
    align-items:center !important;
    justify-content:center !important;
    gap:6px !important;
    height:29px !important;
    min-width:90px !important;
    padding:0 10px !important;
    border:1px solid #315b8a !important;
    border-radius:6px !important;
    background:#315b8a !important;
    color:#fff !important;
    box-shadow:none !important;
    font-size:10px !important;
    font-weight:600 !important;
    line-height:27px !important;
}

.btn-actions:hover,
.btn-actions:focus{
    color:#fff !important;
    background:#274c76 !important;
    transform:none !important;
    box-shadow:none !important;
}
</style>


<!-- =========================================================
     TODAY OPERATIONS PROFESSIONAL POPUPS V16
========================================================= -->
<style>
/* Operation cards clearly clickable but never move. */
.ora-live-operations-row .ora-detail-card{
    cursor:pointer !important;
}
.ora-live-operations-row .ora-detail-card:hover .card{
    transform:none !important;
    box-shadow:0 6px 20px rgba(23,32,51,.07) !important;
    border-color:#d6dee8 !important;
}

/* Unified compact operational popup */
#ContentPlaceHolder2_CheckInpopup,
#ContentPlaceHolder2_Availablepopup,
#ContentPlaceHolder2_Occupypopup,
#ContentPlaceHolder2_Dirtyrooompopup,
#ContentPlaceHolder2_changestatuspopup{
    width:min(1120px,94vw) !important;
    max-height:78vh !important;
    background:#fff !important;
    border:1px solid #dfe5ec !important;
    border-radius:8px !important;
    box-shadow:0 24px 65px rgba(15,23,42,.22) !important;
    overflow:hidden !important;
}

#ContentPlaceHolder2_CheckInpopup .popupheaderforpopup,
#ContentPlaceHolder2_Availablepopup .popupheaderforpopup,
#ContentPlaceHolder2_Occupypopup .popupheaderforpopup,
#ContentPlaceHolder2_Dirtyrooompopup .popupheaderforpopup,
#ContentPlaceHolder2_changestatuspopup .popupheaderforpopup{
    min-height:52px !important;
    padding:10px 14px !important;
    background:#fbfcfe !important;
    border-bottom:1px solid #e3e8ef !important;
}

#ContentPlaceHolder2_CheckInpopup .headertext,
#ContentPlaceHolder2_Availablepopup .headertext,
#ContentPlaceHolder2_Occupypopup .headertext,
#ContentPlaceHolder2_Dirtyrooompopup .headertext,
#ContentPlaceHolder2_changestatuspopup .headertext{
    color:#172033 !important;
    font-size:13px !important;
    font-weight:650 !important;
    text-transform:none !important;
    letter-spacing:0 !important;
}

/* Table popups */
#ContentPlaceHolder2_CheckInpopup .repeaterheight,
#ContentPlaceHolder2_Availablepopup .repeaterheight,
#ContentPlaceHolder2_Occupypopup .repeaterheight,
#ContentPlaceHolder2_changestatuspopup .repeaterheight{
    max-height:calc(78vh - 53px) !important;
    overflow:auto !important;
    margin:0 !important;
    padding:0 !important;
}

#ContentPlaceHolder2_CheckInpopup table,
#ContentPlaceHolder2_Availablepopup table,
#ContentPlaceHolder2_Occupypopup table,
#ContentPlaceHolder2_changestatuspopup table{
    width:100% !important;
    min-width:860px !important;
    margin:0 !important;
    border-collapse:collapse !important;
    background:#fff !important;
}

#ContentPlaceHolder2_CheckInpopup thead,
#ContentPlaceHolder2_Availablepopup thead,
#ContentPlaceHolder2_Occupypopup thead,
#ContentPlaceHolder2_changestatuspopup thead{
    position:sticky !important;
    top:0 !important;
    z-index:2 !important;
}

#ContentPlaceHolder2_CheckInpopup th,
#ContentPlaceHolder2_Availablepopup th,
#ContentPlaceHolder2_Occupypopup th,
#ContentPlaceHolder2_changestatuspopup th{
    padding:8px 9px !important;
    background:#f7f9fc !important;
    color:#475467 !important;
    border:0 !important;
    border-right:1px solid #e3e8ef !important;
    border-bottom:1px solid #e3e8ef !important;
    font-size:9.5px !important;
    font-weight:650 !important;
    text-transform:none !important;
    white-space:nowrap !important;
}

#ContentPlaceHolder2_CheckInpopup td,
#ContentPlaceHolder2_Availablepopup td,
#ContentPlaceHolder2_Occupypopup td,
#ContentPlaceHolder2_changestatuspopup td{
    padding:8px 9px !important;
    color:#344054 !important;
    background:#fff !important;
    border:0 !important;
    border-right:1px solid #edf0f4 !important;
    border-bottom:1px solid #edf0f4 !important;
    font-size:10px !important;
    line-height:1.35 !important;
    white-space:nowrap !important;
}

#ContentPlaceHolder2_CheckInpopup tbody tr:nth-child(even) td,
#ContentPlaceHolder2_Availablepopup tbody tr:nth-child(even) td,
#ContentPlaceHolder2_Occupypopup tbody tr:nth-child(even) td,
#ContentPlaceHolder2_changestatuspopup tbody tr:nth-child(even) td{
    background:#fbfcfe !important;
}

#ContentPlaceHolder2_CheckInpopup tbody tr:hover td,
#ContentPlaceHolder2_Availablepopup tbody tr:hover td,
#ContentPlaceHolder2_Occupypopup tbody tr:hover td,
#ContentPlaceHolder2_changestatuspopup tbody tr:hover td{
    background:#f5f8fb !important;
}

/* Room-list popups */
#ContentPlaceHolder2_Dirtyrooompopup .repeaterheight2,
#ContentPlaceHolder2_Availablepopup .repeaterheight2,
#ContentPlaceHolder2_Occupypopup .repeaterheight2{
    max-height:calc(78vh - 53px) !important;
    padding:12px !important;
    overflow:auto !important;
    background:#f8fafc !important;
}

#ContentPlaceHolder2_Dirtyrooompopup .category,
#ContentPlaceHolder2_Availablepopup .category,
#ContentPlaceHolder2_Occupypopup .category{
    margin-bottom:10px !important;
    padding:10px !important;
    background:#fff !important;
    border:1px solid #e3e8ef !important;
    border-radius:7px !important;
    box-shadow:none !important;
}

#ContentPlaceHolder2_Dirtyrooompopup .item,
#ContentPlaceHolder2_Availablepopup .item,
#ContentPlaceHolder2_Occupypopup .item{
    min-width:64px !important;
    margin:3px !important;
    padding:8px 9px !important;
    background:#fff !important;
    border:1px solid #dfe5ec !important;
    border-radius:6px !important;
    box-shadow:none !important;
    text-align:center !important;
}

/* Fast overlay, no blur rendering cost */
#ContentPlaceHolder2_overlay,
.screenblur{
    background:rgba(15,23,42,.32) !important;
    backdrop-filter:none !important;
    -webkit-backdrop-filter:none !important;
    animation:none !important;
    transition:none !important;
}
</style>

</asp:Content>
<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder2" runat="server">
    <asp:HiddenField runat="server" Value="0" ID="hfMonthWise" />
    <style>
        .card{
    margin-bottom:6px;
}
    </style>

<div class="ora-v6-header" style="display:flex;justify-content:space-between;align-items:center;gap:18px;margin:0 0 12px 0;padding:12px;background:#fff;border:1px solid #e3e8ef;box-shadow:0 5px 18px rgba(23,32,51,.055);">
    <div class="ora-v6-heading">
        <h1 style="margin:0;color:#172033;font-size:24px;line-height:1.15;font-weight:650;letter-spacing:-.3px;">Dashboard</h1>
        <p style="margin:4px 0 0;color:#6b778c;font-size:12px;">Executive hotel performance, operations and reservation exceptions</p>
    </div>

    <div class="ora-v6-datebar" style="display:flex;align-items:flex-end;gap:8px;min-width:620px;">
        <div style="flex:1;min-width:420px;">
            <label style="display:block;margin-bottom:5px;color:#435166;font-size:10px;font-weight:600;letter-spacing:.3px;text-transform:uppercase;">Date Range</label>
            <div class="input-group" style="position: relative;margin-top:5px;">
        <asp:TextBox ID="txt_dateRange" runat="server" CssClass="form-control" ReadOnly="true" placeholder="dd/mm/yyyy - dd/mm/yyyy" />
       <asp:HiddenField ID="hd" runat="server" />
<asp:HiddenField ID="hd1" runat="server" />
        
        <div class="input-group-addon calendar-icon"  onclick="showCalendarFor('<%= txt_dateRange.ClientID %>', '<%= hd.ClientID %>', '<%= hd1.ClientID %>', 'range')" style="cursor:pointer; background-color:#f9a917; height: 30px; width: 30px;">
            📅
        </div>

        <!-- Calendar popup inside same input-group for correct positioning -->
  <div class="calendar-popup" id="calendarPopup">
            <div class="calendar-header" style="display: flex; justify-content: space-between;">
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
                    <button class="btn btn-clear" onclick="clearSelection()">Clear</button>
                    <button class="btn btn-apply" type="button" onclick="applySelection()">Apply</button>
                </div>
            </div>
        </div>
    </div>
        </div>
        <div style="width:120px;">
            <asp:Button ID="btnShow" OnClick="BtnShowData" Text="SHOW" Font-Size="12px" runat="server" CssClass="btnform-control" style="height:32px; margin-top:5px; padding:2px 20px;" />
        </div>
    </div>
</div>
  
<asp:UpdatePanel ID="UpdatePanel2" runat="server" UpdateMode="Conditional">
   <ContentTemplate>
         <div class="container-fluid card" style="margin-top: 10px; padding:0px; background-color: #e3e1e1;">
             <div class="row">
                <div class="col-md-3 hover-button d-none card m-0" runat="server" id="financiallabel">
                    <asp:LinkButton ID="LinkButtonFinancial" runat="server" Text="Financial" OnClick="Label_Click" style="color:Black;"/>
                </div>
                <div class="col-md-3 hover-button d-none card m-0" runat="server" id="Reservationlabel">
                   <%-- <asp:LinkButton ID="LinkButtonReservation" runat="server" Text="Reservation/Check-In(s)" OnClick="Label_Click" style="color:Black;"/>--%>
                    <asp:LinkButton ID="LinkButtonReservation" runat="server" Text="Reservation/Check-In(s)"  style="color:Black;"/>
                </div>
                <div class="col-md-3  hover-button card m-0 d-none" runat="server" id="Roomlabel">
                    <div class="row justify-content-between" style="align-items:center;">
                        <asp:LinkButton class="col-10" ID="LinkButtonRoom" runat="server" Text="Room"  style="color:Black;"/>  
                        <div class=" col-2 recording-symbol">
                            <div class="blinking-dot"></div>
                        </div>
                    </div>
                </div>
                <div class="col-md-3 hover-button card m-0 d-none" runat="server" id="Teamlabel">
                    <asp:LinkButton ID="LinkButtonTeamCustomer" runat="server" Text="Staff & Customer"  style="color:Black;"/>
                </div>
            </div>
             <div style="padding:5px; display:none;" runat="server" id="topfinancialdiv">
                <div class="row">
                        <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showrevenue()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg" runat="server" ID="Image16" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
    <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
    <asp:Label CssClass="cardvalue" ID="sales" style="width:90px; text-align:left;" runat="server" Text="0"></asp:Label><br />
    </div>

                                    <div >
                                        <div class="perdentagediv">
                                        <asp:Image CssClass="imagesize" runat="server" ID="saleimg"/> <asp:Label class="card-percentage" id="salepercentage" runat="server" ></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1">Total Revenue </label>
                                </div> 
                            </div>
                        </div>   
                        <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showPendingPay()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg"  runat="server" ID="Image3" src="img/warning.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="Pendingpay"  style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                  
                                    </div>
                                    <div>
                                        <div class=" col-2 recording-symbol">
                                            <div class="blinking-dot"></div>
                                        </div>
                                        <div class="perdentagediv" >
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgpendingpay"/> <asp:Label id="percentagependingpay" runat="server" class="card-percentage"></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1"> Receivables </label>
                                </div>
                            </div>
                        </div>
                        <div class="ora-security-card col-md-3 col-sm-6 col-6 " style="display:none !important;margin:0px; padding:0px;" onclick="showroomsecurity()">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div>
                                        <asp:Image  class="cardstimg"  runat="server" ID="Image12" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="text-align:center;">
                                    <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                    <asp:Label class="cardvalue" ID="Payableroomsecuritytxt" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
               
                                </div>
                                <div>
                                    <div class=" col-2 recording-symbol">
                                        <div class="blinking-dot"></div>
                                    </div>
                                    <%--<div class="perdentagediv">
                                                <asp:Image CssClass="imagesize" runat="server" ID="imgroomsecurity"/> <asp:Label id="percentageroomsecurity" runat="server" class="card-percentage"></asp:Label>
                                    </div>--%>
                                </div>
                            </div>
                            <div style="text-align:center;">
                                    <label class="cardvalue1">Security Deposit </label>
                            </div>  
                        </div>
                        </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="display:none; margin:0px; padding:0px;" onclick="showrefund()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg"  runat="server" ID="Image10" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="lblrefund" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                    
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                                <asp:Image CssClass="imagesize" runat="server" ID="imgrefund"/> <asp:Label id="refundpercentage" runat="server" class="card-percentage"></asp:Label>  
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1"> Refunded Amount  </label>
                                </div>
                            </div>
                        </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="display:none !important; margin:0px; padding:0px;">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg"  runat="server" ID="Image8" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="Purchasing"    style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                   
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgPurchasing"/> <asp:Label id="percentagePurchasing" runat="server" class="card-percentage"></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1">Purchasing Inventory Items</label>
                                </div>      
                            </div>
                        </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="display:none !important; margin:0px; padding:0px;">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div >
                                    <asp:Image class="cardstimg"  runat="server" ID="Image13" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="text-align:center;">
                                    <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                    <asp:Label class="cardvalue" ID="payable" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                
                                </div>
                                <div>
                                    <div class="perdentagediv">
                                    <asp:Image CssClass="imagesize" runat="server" ID="imgpayable"/> <asp:Label id="percentagepayable" runat="server" class="card-percentage"></asp:Label>
                                    </div>
                                </div>
                            </div>
                            <div style="text-align:center;">
                                <label class="cardvalue1"> Invoice Received </label>
                            </div>
                        </div>
                    </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="display:none !important; margin:0px; padding:0px;" >
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg"  runat="server" ID="Image9" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="paymentstxt" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                 
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgpaymentsmn"/> <asp:Label id="percentagepayments" runat="server" class="card-percentage"></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                    <label class="cardvalue1"> Supplier Payments </label>
                                </div>    
                            </div>
                        </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg" runat="server" ID="Image7" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="Expense"    style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                  
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgexpense"/> <asp:Label id="percentageexpense" runat="server" class="card-percentage"></asp:Label>
                                            </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1">Total Expenses</label>
                                </div>
                            </div>
                        </div>
                        <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                            <asp:Image class="cardstimg"  runat="server" ID="Image17" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <span style="font-size:20px;"><%= Session["currency_symbol"] %></span>
                                        <asp:Label class="cardvalue" ID="Profit_Loss"  style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgProfit_Loss"/> <asp:Label id="Label18" runat="server" class="card-percentage"></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                        <label class="cardvalue1"> Profit/Loss </label>
                                </div>   
                            </div>
                        </div> 
                
<div class="col-md-3 ora-kpi-moved ora-kpi-noshow col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showNOsHOW()">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div>
                                    <asp:Image class="cardstimg" runat="server" ID="Image1" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="text-align:center;">
                                    <asp:Label class="cardvalue" ID="noshow" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                  
                                </div>
                                <div>
                                    <div class="perdentagediv">
                                         <asp:Image CssClass="imagesize" runat="server" ID="imgnoshow"/> <asp:Label id="percentagenoshow" runat="server" class="card-percentage"></asp:Label>
                                    </div>
                                </div>
                            </div>
                            <div style="text-align:center;">
                                     <label class="cardvalue1">No Show </label>
                            </div>   
                        </div>
                </div>
<div class="col-md-3 ora-kpi-moved ora-kpi-cancel col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showCancellation()">
                      <div class="cardst card-hover" style="padding:10px 5px;">
                          <div style="display:flex; justify-content:space-between; width:100%;">
                              <div >
                                   <asp:Image class="cardstimg"  runat="server" ID="Image4" src="img/tick.png"/>
                              </div>
                              <div class="card-valuelabel" style="text-align:center;">
                                  <asp:Label class="cardvalue" ID="cancellation"   style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                 
                              </div>
                              <div>
                                  <div class="perdentagediv" >
                                  <asp:Image CssClass="imagesize" runat="server" ID="imgcancellation"/> <asp:Label id="percentagecancellation" runat="server" class="card-percentage"></asp:Label>
                                  </div>
                              </div>
                          </div>
                          <div style="text-align:center;">
                                  <label class="cardvalue1">Cancellation's </label>
                          </div>
                      </div>
                 </div>
</div>
             </div>
             <div style="padding:0; display:none;" runat="server" id="topReservationdiv" class="ora-hidden-reservation-section">
                 <div class="row">
                    <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showCheckin()">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div>
                                    <asp:Image class="cardstimg" runat="server" ID="Image6" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="text-align:center;">
                                    <span style="font-size:20px;"></span>
                                    <asp:Label class="cardvalue" ID="CheckIn" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                   
                                </div>
                                <div>
                                    <div class="perdentagediv">
                                            <asp:Image CssClass="imagesize" runat="server" ID="imgcheckin"/> <asp:Label id="checkinpercentage" runat="server" class="card-percentage"></asp:Label>
                                    </div>
                                </div>
                            </div>
                            <div style="text-align:center;">
                                <label class="cardvalue1">Check In(s)</label>
                            </div>
                        </div>
                  </div>
                    <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showPendings()">
                       <div class="cardst card-hover" style="padding:10px 5px;">
                           <div style="display:flex; justify-content:space-between; width:100%;">
                               <div >
                                   <asp:Image class="cardstimg"  runat="server" ID="Image18" src="img/warning.png"/>
                               </div>
                               <div class="card-valuelabel" style="text-align:center;">
                                   <span style="font-size:20px;"></span>
                                   <asp:Label class="cardvalue" ID="reservation" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                               </div>
                               <div>
                                   <div class=" col-2 recording-symbol">
                                        <div class="blinking-dot"></div>
                                   </div>
                                   <%--<div  class="perdentagediv">
                                          <asp:Image CssClass="imagesize" runat="server" ID="reservationimg"/> <asp:Label id="reservationpercentage" runat="server" class="card-percentage"></asp:Label>
                                   </div>--%>
                               </div>
                           </div>
                           <div style="text-align:center;">
                                   <label class="cardvalue1">Expected Arrivals / Scheduled</label>
                           </div>   
                       </div>
                 </div>
                      
                   
                <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;">
                          <div class="cardst card-hover" style="padding:10px 5px;">
                              <div style="display:flex; justify-content:space-between; width:100%;">
                                  <div >
                                     <asp:Image class="cardstimg"  runat="server" ID="Image25" src="img/tick.png"/>
                                  </div>
                                  <div class="card-valuelabel" style="text-align:center;">
                                      <asp:Label class="cardvalue" ID="expcheckin" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                                  </div>
                                  <div>
                                      <div class="perdentagediv">
                                           <%--<asp:Image CssClass="imagesize" runat="server" ID="imgcheckout"/> <asp:Label id="checkoutpercentage" runat="server" class="card-percentage"></asp:Label>--%>
                                      </div>
                                  </div>
                              </div>
                              <div style="text-align:center;">
                                        <label class="cardvalue1">Expected Arrivals</label>
                               </div>
                          </div>
                    </div>
                      <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;">
                          <div class="cardst card-hover" style="padding:10px 5px;">
                              <div style="display:flex; justify-content:space-between; width:100%;">
                                  <div>
                                     <asp:Image class="cardstimg"  runat="server" ID="Image26" src="img/tick.png"/>
                                  </div>
                                  <div class="card-valuelabel" style="text-align:center;">
                                       <asp:Label class="cardvalue" ID="expCheckout" style="width:90px; text-align:left;" runat="server">0</asp:Label><br /> 
                                  </div>
                                  <div>
                                      <div class="perdentagediv">
                                           <%--<asp:Image CssClass="imagesize" runat="server" ID="imgcheckout"/> <asp:Label id="checkoutpercentage" runat="server" class="card-percentage"></asp:Label>--%>
                                      </div>
                                  </div>
                              </div>
                              <div style="text-align:center;">
                                   <label class="cardvalue1">Expected Departures</label>
                              </div>
                          </div>
                    </div> 
                      <asp:Repeater ID="bookingrepeater" runat="server" Visible="false">
                           <ItemTemplate>
                               <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;">
                                   <div class="cardst card-hover" style="padding:10px 5px;">
                                       <div style="display:flex; justify-content:space-between; width:100%;">
                                           <div >
                                                <asp:Image class="cardstimg"  runat="server" ID="Image9" src="img/tick.png"/>
                                           </div>
                                           <div class="card-valuelabel" style="text-align:center;">
                                              <label class="cardvalue"> <%# Eval("StatusCount") %></label><br />
                                           </div>
                                           <div>
                                               <div class="perdentagediv" style="width:10%;">
                                                  <asp:Image runat="server" ID="img" style="text-align: right;" src='<%# Eval("img") %>' height="20px" Width="20px"></asp:Image>
                                              </div>
                                           </div>
                                       </div>
                                        <div style="text-align:center;">
                                               <label class="cardvalue1"> <%# Eval("Status") %> </label>
                                       </div>
                                   </div>
                               </div>
                           </ItemTemplate>
                        </asp:Repeater>
                  </div>
             </div>
             <div style="padding:5px; display:none;" runat="server" id="topRoomdiv">
                 <div class="row">
                      <div class="col-md-3 col-sm-6 col-6" style="margin:0px; padding:0px;" onclick="showTotal()">
                           <div class="cardst card-hover" style="padding:10px 5px;">
                               <div style="display:flex; justify-content:space-between; width:100%;">
                                   <div>
                                           <asp:Image class="cardstimg"  runat="server" ID="Image22" src="img/tick.png"/>
                                   </div>
                                   <div class="card-valuelabel" style="text-align:center;">
                                       <asp:Label class="cardvalue" ID="totalrooms" Text="0" style="width:90px; text-align:left;" runat="server"></asp:Label><br />
                                   </div>
                                     <div style="width:10%">
                                   </div>              
                               </div>  
                                <div style="text-align:center;">
                                     <label class="cardvalue1">Total Rooms</label>
                                </div>
                           </div>
                    </div> 
                      <div class="col-md-3 col-sm-6 col-6" style="margin:0px; padding:0px;" onclick="showOccupancy()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div>
                                        <asp:Image class="cardstimg"  runat="server" ID="Image2" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <asp:Label class="cardvalue" ID="occupiedrooms" Text="0" style="width:90px; text-align:left;" runat="server"></asp:Label><br />
                                    </div>
                                      <div style="width:10%">
                                    </div>              
                                </div>  
                                 <div style="text-align:center;">
                                      <label class="cardvalue1">Rooms Occupied</label>
                                 </div>
                            </div>
                     </div> 
                      <div class="col-md-3 col-sm-6 col-6" style="margin:0px; padding:0px;" onclick="showAvailable()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                        <asp:Image class="cardstimg"  runat="server" ID="Image32" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <asp:Label class="cardvalue" Text="0" ID="availablerooms" runat="server"></asp:Label><br />
                                    </div>
                                    <div style="width:10%">
                                     </div>
                                </div>
                                <div style="text-align:center;">
                                    <label class="cardvalue1">Available Rooms</label>
                                </div>
                            </div>
                      </div>
                      <div class="col-md-3 col-sm-6 col-6" style="margin:0px; padding:0px;" onclick="showblocked()">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div >
                                        <asp:Image class="cardstimg"  runat="server" ID="Image23" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="text-align:center;">
                                    <asp:Label class="cardvalue" ID="blockedroomstxt" Text="0" style="width:90px; text-align:left;" runat="server"></asp:Label><br />
                                </div>
                                    <div style="width:10%">
                                </div>              
                            </div>  
                            <div style="text-align:center;">
                                    <label class="cardvalue1">Out Of Order Rooms</label>
                            </div>
                        </div>
                    </div> 
                      <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showDirtyRooms()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                       <asp:Image class="cardstimg"  runat="server" ID="Image20" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <asp:Label class="cardvalue" ID="CheckOut" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                             <%--<asp:Image CssClass="imagesize" runat="server" ID="imgcheckout"/> <asp:Label id="checkoutpercentage" runat="server" class="card-percentage"></asp:Label>--%>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                          <label class="cardvalue1">Dirty Rooms</label>
                                 </div>
                            </div>
                      </div> 
                      <div class="col-md-3  col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="showcurrentCheckin()">
                           <div class="cardst card-hover" style="padding:10px 5px;">
                               <div style="display:flex; justify-content:space-between; width:100%;">
                                   <div >
                                      <asp:Image class="cardstimg"  runat="server" ID="Image24" src="img/tick.png"/>
                                   </div>
                                   <div class="card-valuelabel" style="text-align:center;">
                                       <asp:Label class="cardvalue" ID="txtcurrentguests" style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                 
                                   </div>
                                   <div>
                                       <div class="perdentagediv">
                                            <%--<asp:Image CssClass="imagesize" runat="server" ID="imgcheckout"/> <asp:Label id="checkoutpercentage" runat="server" class="card-percentage"></asp:Label>--%>
                                       </div>
                                   </div>
                               </div>
                               <div style="text-align:center;">
                                         <label class="cardvalue1">Guest In House</label>
                                </div>
                           </div>
                     </div> 
                 </div>
             </div>
             <div style="padding:5px; display:none;" runat="server" id="topTeamdiv">
                <div class="row">
                     <asp:Repeater ID="individualstaffrepeater" runat="server" >
                          <ItemTemplate>
                              <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;">
                                  <div class="cardst card-hover" style="padding:10px 5px;">
                                      <div style="display:flex; justify-content:space-between; width:100%;">
                                          <div >
                                               <asp:Image class="cardstimg"  runat="server" ID="Image9" src="img/tick.png"/>
                                          </div>
                                          <div class="card-valuelabel" style="text-align:center;">
                                             <label class="cardvalue"> <%# Eval("Status_count") %></label><br />
            
                                          </div>
                                          <div>
                                              <div class="perdentagediv" style="width:10%;">
                                                <%-- <asp:Image runat="server" ID="img" style="text-align: right;" src='<%# Eval("img") %>' height="20px" Width="20px"></asp:Image>--%>
                                             </div>
                                          </div>
                                      </div>
                                       <div style="text-align:center;">
                                              <label class="cardvalue1"> <%# Eval("Department") %> </label>
                                      </div>
                                  </div>
                              </div>
                          </ItemTemplate>
                       </asp:Repeater>
                     <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="SHOWSTAFF()">
                        <div class="cardst card-hover" style="padding:10px 5px;">
                            <div style="display:flex; justify-content:space-between; width:100%;">
                                <div>
                                     <asp:Image class="cardstimg"  runat="server" ID="Image21" src="img/tick.png"/>
                                </div>
                                <div class="card-valuelabel" style="display: flex; text-align:center;">
                                    <asp:Label class="cardvalue" ID="presentstf" runat="server">0</asp:Label>
                                    <asp:Label class="cardvalue" ID="Label6" runat="server">/</asp:Label>
                                    <asp:Label class="cardvalue" ID="totalstf"  style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
                                </div>
                            </div>  
                            <div style="text-align:center;">
                                 <label class="cardvalue1"> Total Staff </label>
                            </div>
                        </div>
                     </div>
                     <div class="col-md-3 col-sm-6 col-6 " style="margin:0px; padding:0px;" onclick="SHOWTotalCustomer()">
                            <div class="cardst card-hover" style="padding:10px 5px;">
                                <div style="display:flex; justify-content:space-between; width:100%;">
                                    <div >
                                         <asp:Image class="cardstimg"  runat="server" ID="Image15" src="img/tick.png"/>
                                    </div>
                                    <div class="card-valuelabel" style="text-align:center;">
                                        <asp:Label class="cardvalue" ID="guest"  style="width:90px; text-align:left;" runat="server">0</asp:Label><br />
  
                                    </div>
                                    <div>
                                        <div class="perdentagediv">
                                         <asp:Image CssClass="imagesize" runat="server" ID="guestimg"/> <asp:Label id="guestpercentage" runat="server" class="card-percentage"></asp:Label>
                                        </div>
                                    </div>
                                </div>
                                <div style="text-align:center;">
                                     <label class="cardvalue1"> Registered Guests </label>
                                </div>  
                            </div>
                     </div>
                     
                </div>
             </div>
         </div>   
   </ContentTemplate>
</asp:UpdatePanel>
    <style>
        .p-col{
            padding:5px !important;
        }
    </style>
    <div class="ora-today-operations-title">
        <h5>Today’s Operations</h5>
        <span>Live room and guest movement status</span>
    </div>
    <div class="row ora-live-operations-row">
    <!-- Check-ins -->
    <div class="col-md-2 p-col ora-detail-card" onclick="showCheckin()">
        <asp:HiddenField ID="hfCheckedInToday" runat="server" />
<asp:HiddenField ID="hfExpectedCheckIns" runat="server" />
      <div class="card checkin">
          
        <h6 class="text-info">Check-ins           <span class="info-icon" data-tooltip="Total check in today / Expected Checkins today ">
    <i class="fas fa-info-circle"></i>
</span></h6>

        <div class="progress-ring">
          <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
            <circle class="bg" cx="60" cy="60" r="54"></circle>
            <circle class="progress" cx="60" cy="60" r="54"></circle>
          </svg>
          <div class="progress-text text-info" runat="server" id="divcheckedintoday">0</div>
        </div>
        <div class="progress-subtext">of <asp:Label runat="server" style="font-size:20px;" ID="lbltotlchkexpected"></asp:Label></div>
      </div>
    </div>
    <!-- Check-outs -->
    <div class="col-md-2 p-col ora-detail-card" onclick="showCheckout()">
           <asp:HiddenField ID="hdntodaycheckout" runat="server" />
<asp:HiddenField ID="hdntotalexpectedcheckout" runat="server" />
      <div class="card checkout">
        <h6 class="text-danger">Check-outs <span class="info-icon" data-tooltip="Total check outs today / Expected Checkouts today">
    <i class="fas fa-info-circle"></i>
</span></h6>
        <div class="progress-ring">
          <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
            <circle class="bg" cx="60" cy="60" r="54"></circle>
            <circle class="progress" cx="60" cy="60" r="54"></circle>
          </svg>
          <div class="progress-text text-danger" runat="server" id="divcheckoutcomplete">0</div>
        </div>
        <div class="progress-subtext">of <asp:Label runat="server" style="font-size:20px;" ID="lbltotalexpectedcheckout"></asp:Label></div>
      </div>
    </div>
    <!-- Available Rooms -->
    <div class="col-md-2 p-col ora-detail-card" onclick="showAvailable()">
                        <asp:HiddenField ID="hdntotalrooms" runat="server" />
<asp:HiddenField ID="hdnavailablerooms" runat="server" />

      <div class="card rooms">
        <h6 class="text-primary">Available Rooms <span class="info-icon" data-tooltip="Available rooms/Total">
    <i class="fas fa-info-circle"></i>
</span></h6>
        <div class="progress-ring">
          <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
            <circle class="bg" cx="60" cy="60" r="54"></circle>
            <circle class="progress" cx="60" cy="60" r="54"></circle>
          </svg>
          <div class="progress-text text-primary" runat="server" id="divavailablerooms">0</div>
        </div>
        <div class="progress-subtext">of <asp:Label ID="lbltotalrooms" style="font-size:20px;" runat="server" ></asp:Label></div>
      </div>
    </div>
        <div class="col-md-2 p-col ora-detail-card" onclick="showOccupancy()">
  <div class="card rooms1">
    <asp:HiddenField ID="hdnoccupiedrooms" runat="server" />
    <h6 class="text-primary">Occupied Rooms <span class="info-icon" data-tooltip="Occupied rooms/Total">
    <i class="fas fa-info-circle"></i>
</span></h6>
    <div class="progress-ring">
      <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
        <circle class="bg" cx="60" cy="60" r="54"></circle>
        <circle class="progress" cx="60" cy="60" r="54"></circle>
      </svg>
      <div class="progress-text text-primary" runat="server" id="divtotaloccupiedrooms">0</div>
    </div>
    <div class="progress-subtext">of <asp:Label ID="lbltotalrooms1" style="font-size:20px;" runat="server" ></asp:Label></div>
  </div>
</div>
  <div class="col-md-2 p-col ora-detail-card" onclick="showblocked()">
  <div class="card rooms2">
    <asp:HiddenField ID="hdnblockedRooms" runat="server" />
    <asp:HiddenField ID="hdnDirtyRooms" runat="server" />
    <h6 class="text-primary">Blocked Rooms<span class="info-icon" data-tooltip="Blocked Rooms/Total">
    <i class="fas fa-info-circle"></i>
</span></h6>
    <div class="progress-ring">
      <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
        <circle class="bg" cx="60" cy="60" r="54"></circle>
        <circle class="progress" cx="60" cy="60" r="54"></circle>
      </svg>
      <div class="progress-text text-primary" runat="server" id="divblockedrooms">0</div>
    </div>
    <div class="progress-subtext">of <asp:Label ID="lbltotalrooms2" style="font-size:20px;" runat="server" ></asp:Label></div>
  </div>
</div>
   <div class="col-md-2 p-col ora-detail-card" onclick="showDirtyRooms()">
  <div class="card rooms3">
    <asp:HiddenField ID="HiddenField3" runat="server" />
    <h6 class="text-primary">Dirty Rooms<span class="info-icon" data-tooltip="Dirty Rooms/Total">
    <i class="fas fa-info-circle"></i>
</span></h6>
    <div class="progress-ring">
      <svg viewBox="0 0 120 120" width="120" height="120" preserveAspectRatio="xMidYMid meet">
        <circle class="bg" cx="60" cy="60" r="54"></circle>
        <circle class="progress" cx="60" cy="60" r="54"></circle>
      </svg>
      <div class="progress-text text-primary" runat="server" id="divdirtyroom">0</div>
    </div>
    <div class="progress-subtext">of <asp:Label ID="lbltotaldirtydiv" style="font-size:20px;" runat="server" ></asp:Label></div>
  </div>
</div>
  </div>
<div class="container-fluid" style="padding-right:0px;">
    
<div class="ora-v6-content-grid">
    
<div class="ora-v6-panel ora-v6-bookings" style="background:#fff;border:1px solid #e3e8ef;box-shadow:0 5px 18px rgba(23,32,51,.055);overflow:hidden;">
    <div style="display:flex;justify-content:space-between;align-items:center;padding:11px 13px;border-bottom:1px solid #e3e8ef;background:#fbfcfe;">
        <div>
            <div style="color:#172033;font-size:13px;font-weight:650;">
                Bookings - <asp:Label runat="server" ID="reservationdetailyear"></asp:Label>
            </div>
            <div style="margin-top:3px;color:#6b778c;font-size:9px;">Monthly reservation volume by booking source</div>
        </div>

        <asp:DropDownList runat="server" ID="dropdownlistyear"
            OnSelectedIndexChanged="changeyear" AutoPostBack="true"
            style="width:86px;height:36px;padding:0 9px;border:1px solid #ccd5df;border-radius:0;background:#fff;color:#172033;font-size:11px;font-weight:600;">
            <asp:ListItem Text="2024" Value="2024" />
            <asp:ListItem Text="2025" Value="2025" />
            <asp:ListItem Text="2026" Value="2026" />
            <asp:ListItem Text="2027" Value="2027" />
            <asp:ListItem Text="2028" Value="2028" />
            <asp:ListItem Text="2029" Value="2029" />
            <asp:ListItem Text="2030" Value="2030" />
            <asp:ListItem Text="2031" Value="2031" />
            <asp:ListItem Text="2032" Value="2032" />
            <asp:ListItem Text="2033" Value="2033" />
            <asp:ListItem Text="2034" Value="2034" />
            <asp:ListItem Text="2035" Value="2035" />
        </asp:DropDownList>
    </div>

    <div style="height:350px;padding:12px 14px;">
        <asp:Label ID="Label21" runat="server" Visible="false"
            Text="No Record Found" CssClass="card-title" />
        <canvas id="ReservationChartOption" style="display:block;width:100%;height:325px;"></canvas>
    </div>
</div>

    
<div class="ora-v6-panel ora-v6-checkin" style="background:#fff;border:1px solid #e3e8ef;box-shadow:0 5px 18px rgba(23,32,51,.055);overflow:hidden;">
    <div style="padding:11px 13px;border-bottom:1px solid #e3e8ef;background:#fbfcfe;">
        <div style="color:#172033;font-size:13px;font-weight:650;">Check-in Time Analysis</div>
        <div style="margin-top:3px;color:#6b778c;font-size:9px;">Live guest arrival distribution for the selected period</div>
    </div>

    <div id="CheckInTimeSlots" class="ora-v6-time-grid"></div>

    <div class="ora-v6-time-metrics">
        <div class="ora-v6-time-metric">
            <span>Peak check-in slot</span>
            <strong id="CheckInPeakSlot">-</strong>
        </div>
        <div class="ora-v6-time-metric">
            <span>Average check-in time</span>
            <strong id="CheckInAverageTime">-</strong>
        </div>
        <div class="ora-v6-time-metric">
            <span>Late arrivals after 21:00</span>
            <strong id="CheckInLateCount">0</strong>
        </div>
    </div>
</div>

    <div class="ora-v6-inhouse-wrap">
        <div class="col-md-12 ora-inhouse-panel" style="margin-bottom:10px;">
            <div class="dashboard-inhouse-card">
                <div class="dashboard-inhouse-titlebar">
                    <div>
        <h5 class="dashboard-inhouse-title" style="margin:0;color:#172033;font-size:13px;font-weight:650;">Current In-House Guests</h5>
        <div style="margin-top:3px;color:#6b778c;font-size:9px;">Live guest and room assignment list</div>
    </div>
                    <asp:Label ID="lblDashboardInHouseCount" runat="server" CssClass="dashboard-inhouse-count" Text="0" />
                </div>
                <div class="dashboard-inhouse-scroll">
                    <table class="dashboard-inhouse-grid ora-inhouse-required-columns">
                        <thead>
                            <tr>
                                <th style="width:48px;">S.NO</th>
                                <th>Reservation No</th>
                                <th>Guest Name</th>
                                <th>Arrival &amp; Departure</th>
                                <th style="width:75px;">Stays</th>
                                <th>Room Category</th>
                                <th style="width:85px;">Room Number</th>
                                <th>Rate Plan</th>
                                <th class="dashboard-action-col" style="width:105px;">Action</th>
                            </tr>
                        </thead>

                        <tbody>
                            <asp:Repeater ID="rptDashboardInHouseGuests" runat="server" OnItemDataBound="rptDashboardInHouseGuests_ItemDataBound">
                                <ItemTemplate>
                                    <tr>
                                        <td class="ora-inhouse-serial">
                                            <%# Container.ItemIndex + 1 %>
                                        </td>

                                        <td>
                                            <span class="dashboard-inhouse-muted">
                                                <%# DashboardGetText(
                                                    Container.DataItem,
                                                    "reg_id",
                                                    "RegistrationNo",
                                                    "ReservationNo",
                                                    "reservation_no") %>
                                            </span>
                                        </td>

                                        <td>
                                            <span class="dashboard-inhouse-guest">
                                                <%# DashboardGetGuestName(Container.DataItem) %>
                                            </span>
                                        </td>

                                        <td class="ora-stay-dates">
                                            <span>
                                                <%# DashboardFormatAnyDate(
                                                    DashboardGetValue(
                                                        Container.DataItem,
                                                        "ArrivalDate",
                                                        "arrival_date",
                                                        "arr_date")) %>
                                            </span>
                                            <span class="ora-date-arrow">→</span>
                                            <span>
                                                <%# DashboardFormatAnyDate(
                                                    DashboardGetValue(
                                                        Container.DataItem,
                                                        "DepartureDate",
                                                        "departure_date",
                                                        "dep_date")) %>
                                            </span>
                                        </td>

                                        <td class="ora-inhouse-stays">
                                            <%# DashboardGetNightsAny(
                                                DashboardGetValue(
                                                    Container.DataItem,
                                                    "ArrivalDate",
                                                    "arrival_date",
                                                    "arr_date"),
                                                DashboardGetValue(
                                                    Container.DataItem,
                                                    "DepartureDate",
                                                    "departure_date",
                                                    "dep_date")) %>
                                            <span>night(s)</span>
                                        </td>

                                        <td>
                                            <%# DashboardGetText(
                                                Container.DataItem,
                                                "room_category",
                                                "RoomCategory",
                                                "category",
                                                "category_name") %>
                                        </td>

                                        <td>
                                            <span class="dashboard-room-badge">
                                                <%# DashboardGetText(
                                                    Container.DataItem,
                                                    "room_nos",
                                                    "room_no",
                                                    "RoomNo",
                                                    "RoomNumber") %>
                                            </span>
                                        </td>

                                        <td>
                                            <%# DashboardGetText(
                                                Container.DataItem,
                                                "rateplan_names",
                                                "rateplan_name",
                                                "RatePlan",
                                                "rate_plan") %>
                                        </td>

                                        <td class="dashboard-action-col">
                                            <asp:HiddenField ID="hf_status" runat="server"
                                                Value='<%# DashboardGetText(
                                                    Container.DataItem,
                                                    "res_status",
                                                    "Status",
                                                    "status") %>' />

                                            <div class="dashboard-action-wrap">
                                                <button type="button"
                                                    class="btn btn-actions"
                                                    onclick="return DashboardToggleActionMenu(this, event);">
                                                    Actions
                                                    <i class="bi bi-chevron-down"></i>
                                                </button>

                                                <ul class="dropdown-menu dropdown-menu-end action-menu dashboard-action-menu">
                                                    <li id="liCheck" runat="server" visible="false">
                                                        <asp:LinkButton ID="lnkCheck" runat="server"
                                                            CssClass="dropdown-item action-item checkin-item"
                                                            CommandName="checkin"
                                                            CommandArgument='<%# DashboardGetText(
                                                                Container.DataItem,
                                                                "id",
                                                                "reservation_id",
                                                                "ReservationId") %>'
                                                            OnCommand="DashboardGuestAction_Command">
                                                            <i class="bi bi-box-arrow-right"></i>
                                                            Edit Check-in
                                                        </asp:LinkButton>
                                                    </li>

                                                    <li>
                                                        <a class="dropdown-item action-item receipt-item"
                                                            target="_blank"
                                                            rel="noopener"
                                                            onclick="DashboardCloseAllActionMenus();"
                                                            href='<%# DashboardBuildPaymentInvoiceUrl(
                                                                DashboardGetText(
                                                                    Container.DataItem,
                                                                    "reg_id",
                                                                    "RegistrationNo",
                                                                    "ReservationNo",
                                                                    "reservation_no")) %>'>
                                                            <i class="bi bi-receipt-cutoff"></i>
                                                            Payment Invoice
                                                        </a>
                                                    </li>

                                                    <li>
                                                        <asp:LinkButton ID="lnkInvoice" runat="server"
                                                            CssClass="dropdown-item action-item invoice-item"
                                                            CommandName="invoice"
                                                            CommandArgument='<%#
                                                                DashboardGetText(
                                                                    Container.DataItem,
                                                                    "reg_id",
                                                                    "RegistrationNo",
                                                                    "ReservationNo",
                                                                    "reservation_no")
                                                                + "|" +
                                                                DashboardGetText(
                                                                    Container.DataItem,
                                                                    "visit_id",
                                                                    "VisitId",
                                                                    "visitid") %>'
                                                            OnCommand="DashboardGuestAction_Command">
                                                            <i class="bi bi-file-earmark-text-fill"></i>
                                                            Duplicate Invoice
                                                        </asp:LinkButton>
                                                    </li>

                                                    <li>
                                                        <asp:LinkButton ID="lnkHistory" runat="server"
                                                            CssClass="dropdown-item action-item history-item"
                                                            CommandName="history"
                                                            CommandArgument='<%# DashboardGetText(
                                                                Container.DataItem,
                                                                "reg_id",
                                                                "RegistrationNo",
                                                                "ReservationNo",
                                                                "reservation_no") %>'
                                                            OnCommand="DashboardGuestAction_Command">
                                                            <i class="bi bi-clock-history"></i>
                                                            Guest History
                                                        </asp:LinkButton>
                                                    </li>
                                                </ul>
                                            </div>
                                        </td>
                                    </tr>
                                </ItemTemplate>
                            </asp:Repeater>
                        </tbody>
                    </table>
                    <asp:Panel ID="pnlDashboardInHouseEmpty" runat="server" CssClass="dashboard-inhouse-empty" Visible="false">
                        No in-house guests found.
                    </asp:Panel>
                </div>
            </div>
        </div>
    </div>
</div>

     <div class="row d-none justify-content-between" style="padding: 10px; margin-bottom:10px; z-index:999;">
     <div class=" justify-content-between" style="display:flex;  background-color:#d1e1ef;">
        <div style=" display:flex; align-items:end; padding-left:10px;"> 
            <asp:Label runat="server" ID="heading" style=" vertical-align:middle; font-size:18px; margin-left:5px; font-weight:bold; color:#545454;">Reservation Rate</asp:Label>          
        </div>
     </div>
     <div class="row justify-content-between" style="padding:0px;">
         <div style="display:none;">
         <div class="col-md-5 col-12 d-md-block">
             <label style="font-size:11px; white-space:nowrap; font-weight:bold;">Start Date</label>  
             <asp:TextBox ID="TextBoxstart" runat="server" CssClass="form-control" Font-Size="11px" TextMode="Date" AutoPostBack="true"></asp:TextBox>
         </div>
         <div class="col-md-5 col-12 d-md-block">
             <div runat="server" id="Div3">
                 <label style="font-size:11px; white-space:nowrap; font-weight:bold; width:50px;">End Date</label>  
                 <asp:TextBox ID="TextBoxend" runat="server" CssClass="form-control" Font-Size="11px"  TextMode="Date"  AutoPostBack="true"></asp:TextBox>
             </div>
         </div>
         <div class="col-md-2 col-12 d-md-block">
             <label style="font-size:11px; white-space:nowrap; font-weight:bold;">Nights</label>  
             <asp:TextBox ID="TextBoxnights" runat="server" CssClass="form-control" placeholder="0" Font-Size="11px" onkeypress="return isNumberKey(event)"  AutoPostBack="true" ></asp:TextBox> 
         </div>
       </div>
         <script type="text/javascript">
             document.addEventListener("DOMContentLoaded", function () {
                 var startInput = document.getElementById('<%= TextBoxstart.ClientID %>');
                 var endInput = document.getElementById('<%= TextBoxend.ClientID %>');
                 if (startInput && endInput) {
                     var today = new Date().toISOString().split('T')[0];
                     startInput.setAttribute("min", today);
                     endInput.setAttribute("min", today);
                     // Set min end date if start date is already selected
                     if (startInput.value) {
                         endInput.setAttribute("min", startInput.value);
                     }
                     // Update end date min when start date changes
                     startInput.addEventListener("change", function () {
                         if (startInput.value) {
                             endInput.setAttribute("min", startInput.value);
                         }
                     });
                 }
             });
         </script>
         <div class="col-md-12 col-12 d-md-block" style="margin-top:15px;">
             <table style="width:100%;">
               <thead>
                   <tr>
                       <th>SR#</th>
                       <th>CATEGORY</th>
                       <th>PLAN</th>
                       <th>RATE</th>
                       <th>OPTION</th>
                   </tr>
               </thead>
               <tbody>
                   <asp:Repeater runat="server" ID="reservationraterepeater">
                       <ItemTemplate>
                           <tr>
                              <td><%# Container.ItemIndex + 1 %></td>
                              <td><%# Eval("category") %></td>
                              <td><%# Eval("planname") %></td>
                              <td><%# Eval("total_rate") %></td>
                              <td style="text-align:center; display:flex; justify-content:space-evenly; border:none;">
                                 <asp:Button class="btnstyle" Text="Choose" ID="Button5" CommandArgument='<%# Eval("category")+","+Eval("planname") %>' OnClick="choose" runat="server" />
                              </td>
                           </tr>
                       </ItemTemplate>
                   </asp:Repeater>
               </tbody>
            </table>
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
 <script>
     // Function to set progress dynamically
     function setProgress(el, value, total) {
         const circle = el.querySelector('.progress');
         const radius = circle.r.baseVal.value;
         const circumference = 2 * Math.PI * radius;
         const offset = circumference - (value / total) * circumference;
         circle.style.strokeDasharray = circumference;
         circle.style.strokeDashoffset = offset;
     }

     // window.onload = function () {
     // Get values from hidden fields
     var checkedInToday = parseInt(document.getElementById('<%= hfCheckedInToday.ClientID %>').value || 0);
     var expectedCheckIns = parseInt(document.getElementById('<%= hfExpectedCheckIns.ClientID %>').value || 1); // avoid divide by 0
     // Apply donut chart progress
     setProgress(document.querySelector('.checkin'), checkedInToday, expectedCheckIns);
     var checkedOutToday = parseInt(document.getElementById('<%= hdntodaycheckout.ClientID %>').value || 0);
     var expectedCheckOut = parseInt(document.getElementById('<%= hdntotalexpectedcheckout.ClientID %>').value || 1); // avoid divide by 0
     // Apply donut chart progress
     setProgress(document.querySelector('.checkout'), checkedOutToday, expectedCheckOut);

     var totalrooms = parseInt(document.getElementById('<%= hdntotalrooms.ClientID %>').value || 0);
     var avialabelrooms = parseInt(document.getElementById('<%= hdnavailablerooms.ClientID %>').value || 1); // avoid divide by 0
     var occupiedrooms = parseInt(document.getElementById('<%= hdnoccupiedrooms.ClientID %>').value || 1); // avoid divide by 0
     var blockedrooms = parseInt(document.getElementById('<%= hdnblockedRooms.ClientID %>').value || 1); // avoid divide by 0
     var dirtyrooms = parseInt(document.getElementById('<%= hdnDirtyRooms.ClientID %>').value || 1); // avoid divide by 0
     // Apply donut chart progress
     setProgress(document.querySelector('.rooms'), avialabelrooms, totalrooms);
     setProgress(document.querySelector('.rooms1'), occupiedrooms, totalrooms);
     setProgress(document.querySelector('.rooms2'), blockedrooms, totalrooms);
     setProgress(document.querySelector('.rooms3'), dirtyrooms, totalrooms);
     //};
 </script>

       
<asp:UpdatePanel ID="popupUpdatePanel" runat="server" UpdateMode="Conditional">
   <ContentTemplate>
 
    <style>
        .evenRow 
        {
            background-color: #F3F8FF;
        }
        .oddRow 
        {
            background-color: #b1d8b726;
        }
        .popupheaderforpopup
        {
    	    display:flex;
            justify-content:space-between; 
            padding:5px; 
            background-color:#efa63c;
        }
         .headertext
        {
    	    Font-Size:20px; 
    	    font-weight:bold; 
    	    margin-top:5px;
        }
        table, tr, th, td 
        {
            font-size:13px;
        }
    </style>
    <div class="screenblur" style=" display:none;" id="overlay" runat="server"></div>
    <div id="popupex" runat="server" class="popupw">
             
                    <div style="margin-bottom:15px;">
                        <div class="popupheaderforpopup">
                            <asp:Label class="headertext" ID="room" runat="server">PURCHASING DETAILS</asp:Label><asp:ImageButton runat="server" ID="closebtn" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>
                           
                        </div>
                        <div style="padding:5px; display:flex;">
                            <p style="Font-Size:13px; font-weight:bold; margin:0px;">Invoice No# </p><asp:Label Font-Size="13px" ID="popupinvoiceno" runat="server"></asp:Label>
                            <asp:HiddenField ID="hf_roomid" runat="server" />
                        </div> 
                      
                        <div class="repeaterheight" style="overflow:auto;">
                         <table id="Table1">
                                <thead>
                                    <tr style=" text-align:left; ">
                                      <th scope="col">SR# </th>
                                      <th scope="col">SKU NO.</th>
                                      <th scope="col">ITEM</th>
                                      <th scope="col">QTY</th>
                                      <th scope="col">UNIT</th>
                                      <th scope="col">SUPPLIER</th>
                                      <th scope="col">DATE</th>
                                      <th scope="col">Total Pay</th> 
                                    </tr>
                                  </thead>
                                  <tbody>
                                    
                                      <asp:Repeater ID="popuppurchasingrepeater" runat="server">
                                          <ItemTemplate>
                                            <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:14px;">
                                                <td><%# Container.ItemIndex + 1 %></td>
                                                <td style="text-align:right;"><%# Eval("itemcode")%></td>
                                                <td><%# Eval("itemname")%></td>
                                                <td style="text-align:right;"><%# Eval("quantity")%></td>
                                                <td><%# Eval("unit")%></td>
                                                <td><%# Eval("supplier")%></td>
                                                <td><%# Eval("currentdate")%></td>
                                                <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("totalbill") %></td>
                                            </tr>
                                          </ItemTemplate>
                                      </asp:Repeater>

                                  </tbody>
                            </table>
                        </div>  
                        
                    </div>
    </div>  
         
    <div id="changeratepopup" runat="server" class="popupwZ">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label14" runat="server">ROOM RENT</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton3" OnClick="closepopup" height="35px" Width="35px" src="img/icons8-cross-50.png"/>
                            </div>
                            <div style="padding:5px; display:flex; align-items:center; justify-content:space-between;">
                                <p style="Font-Size:11px; margin:5px; ">ROOM </p> <asp:DropDownList runat="server" ID="roomcategorytxt"  CssClass="form-control" Font-Size="11px" height="30px"></asp:DropDownList>
                                <asp:HiddenField ID="HiddenField1" runat="server" />
                                <p style="margin:0px 10px"></p>
                                <p style="Font-Size:11px; margin:5px;  ">RATE </p> <asp:TextBox runat="server" ID="ratetxt" CssClass="form-control" Font-Size="11px" Height="30px" oninput="validateNumberInput(this)" />
                                <script type="text/javascript">
                                    function validateNumberInput(txtBox) {
                                        txtBox.value = txtBox.value.replace(/[^0-9]/g, '');
                                    }
                                </script>

                                <asp:HiddenField ID="HiddenField2" runat="server" />
                                <asp:Button style="margin-left:10px;" width="20%" ID="up" runat="server" Text="Update" CssClass="btnform-control" OnClick="updatechangeroomrate" Font-Size="11px"/>
                                <asp:Button ID="Button1" runat="server" style="display: none;" OnClick="GetDeailsOfDescriptionClickedOnchangerateRepeater" />
                                        
                            </div>
                                                
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table3">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">DESCRIPTION</th>
                                          <th scope="col">RATE</th>
                                          
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="changeraterepeater" runat="server">
                                              <ItemTemplate>
                                                <tr onclick="rowClicked1(this)" class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("description")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("rate")%></td>             
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
                        </div>
    </div>

    <div id="changestatuspopup" runat="server" class="popupwZ">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label15" runat="server">ROOM STATUS</asp:Label><asp:ImageButton runat="server" ID="ImageButton4" OnClick="closepopup" height="35px" Width="35px" src="img/icons8-cross-50.png"/>
                               
                            </div>
                            <div style="padding:5px; display:flex; align-items:center; justify-content:space-between;">
                                
                                <p style="Font-Size:11px; margin:5px; ">ROOM </p><asp:DropDownList runat="server" ID="ROOMNOINROOMSTATUS"  CssClass="form-control" Font-Size="11px" height="30px"/>
                                
                                <p style="margin:0px 10px"></p>
                                <p style="Font-Size:11px; margin:5px;  ">STATUS </p>
                                <asp:DropDownList runat="server" ID="ROOMSTATUSINROOMSTATUS" CssClass="form-control" OnSelectedIndexChanged="showdescriptionofroomstatuspopup" AutoPostBack="true" Font-Size="11px" Height="30px" ClientIDMode="Static">
                                    <asp:ListItem Text="Available" Value="Available"></asp:ListItem>
                                    <asp:ListItem Text="Blocked" Value="Blocked"></asp:ListItem>  
                                </asp:DropDownList>
                                
                                 

                                
                                <asp:HiddenField ID="HiddenField4" runat="server" />
                                <asp:HiddenField ID="HiddenField5" runat="server" />
                                <asp:Button style="margin-left:10px;" width="20%" ID="ROOMSTATUSUPDATE" runat="server" Text="Update" CssClass="btnform-control" OnClick="updatechangeroomstatus" Font-Size="11px"/>
                                <asp:Button  ID="Button2" OnClick="GetDeailsOfDescriptionClickedOnchangestatusRepeater" runat="server" style="display: none;" />
                                        
                            </div>
                             
                            <div id="descriptiontextboxinroomstatuspopup" runat="server" style="width:100%; display:none;padding:10px;">
                            <p style="Font-Size:11px; margin:5px;">DESCRIPTION </p> <asp:TextBox ID="blockdescription" runat="server" CssClass="form-control" TextMode="MultiLine" Rows="3" Font-Size="11px" ></asp:TextBox>
                            </div>
                                                
                           <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table5">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">ROOM NO.</th>
                                          <th scope="col">CATEGORT</th>
                                          <th scope="col">STATUS</th>
                                          <th scope="col">DESCRIPTION</th>
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="changestatusrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr onclick="rowClicked2(this)" class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td style="text-align:right;"><%# Eval("room_no")%></td>
                                                    <td><%# Eval("room_category")%></td>
                                                    <td><%# Eval("room_status")%></td>
                                                    <td><%# Eval("reason")%></td>             
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
                        </div>
    </div>

    <div id="Availablepopup" runat="server" class="popupwZ ora-compact-detail-popup" style="background-color:none;">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label16" runat="server">AVAILABLE ROOMS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton5" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
          <div class="repeaterheight2" style="overflow:auto;">
            <asp:Repeater ID="CategoryRepeater" runat="server" OnItemDataBound="CategoryRepeater_ItemDataBound">
                <ItemTemplate>
                    <div class="category">
                       <b>
                       <img src="img/icons8-double-bed-30.png" style="height:28px; width:20px;" />
                       <asp:Label runat="server" id="lblMessage" style=" text-transform:uppercase;   border-bottom:1px solid black; font-size: 15px; color: #545454;"><%# Eval("RoomCategory") %></asp:Label></b> 

                        <div class="d-flex" style="padding:10px; overflow:auto;">
                            <asp:Repeater ID="RoomRepeater" runat="server">
                                <HeaderTemplate>
                                    <table border="0" cellpadding="0" cellspacing="0"; style="" >
                                        <tr>
                                </HeaderTemplate>

                                <ItemTemplate>
                            
                                            <%# (Container.ItemIndex != 0 && Container.ItemIndex % 10== 0) ? @"</tr><tr>" : string.Empty %>
                                        
                                                <div class="col-md-1 item" id="divRoom" style="background-color:#ffffff;border: 1px solid lightgray;
                                                    border-radius: 15px; 
                                                    margin-bottom: 3px; 
                                                    padding: 10px;
                                                    box-shadow: 0px 0px 10px 2px rgba(0, 0, 0, 0.1);"  onclick="callBackendFunction('<%# Eval("RoomNumber") %>','<%# Eval("RoomCategory") %>','<%# Eval("RoomStatus") %>')">

                                                
                                                    <asp:Label font-size="12px" runat="server" id="label" text='<%# Eval("RoomNumber") %>' font-bold="true" forecolor="#333"></asp:Label><br />
                                                </div>
                                        
                                </ItemTemplate>

                                <FooterTemplate>
                                        </tr>
                                    </table>
                                </FooterTemplate>
                            </asp:Repeater>
                        </div>
                    </div>
                </ItemTemplate>
            </asp:Repeater>
          </div>         
                            
       </div>
    </div>

    <div id="Occupypopup" runat="server" class="popupwZ ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label22" runat="server">OCCUPIED ROOMS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton6" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
          <div class="repeaterheight2" style="overflow:auto;" >
            <asp:Repeater ID="occupyRepeater" runat="server" OnItemDataBound="CategoryRepeater_ItemDataBound">
                <ItemTemplate>
                    <div class="category">
                       <b>
                       <img src="img/icons8-double-bed-30.png" style="height:28px; width:20px;" />
                       <asp:Label runat="server" id="lblMessage" style=" text-transform:uppercase;   border-bottom:1px solid black; font-size: 15px; color: #545454;"><%# Eval("RoomCategory") %></asp:Label></b> 

                        <div class="d-flex" style="padding:10px; overflow:auto;">
                        <asp:Repeater ID="RoomRepeater" runat="server">
                            <HeaderTemplate>
                                <table style="border:0px solid black;">
                                    <tr style="border:0px solid black;">
                            </HeaderTemplate>

                            <ItemTemplate>
                                <%# (Container.ItemIndex != 0 && Container.ItemIndex % 10 == 0) ? @"</tr><tr style='border: 0px;'>" : string.Empty %>
                             
                                    <div class="col-md-1 item" id="divRoom" style="background-color:#ffffff; border: 1px solid lightgray;
                                          border-radius: 15px; margin-bottom: 3px;  padding: 10px; box-shadow: 0px 0px 10px 2px rgba(0, 0, 0, 0.1);">
                                          <asp:Label font-size="12px" runat="server" id="label" text='<%# Eval("RoomNumber") %>' font-bold="true" forecolor="#333"></asp:Label><br />
                                     </div>
                               
                            </ItemTemplate>

                            <FooterTemplate>
                                    </tr>
                                    
                                </table>
                            </FooterTemplate>
                        </asp:Repeater>
                        </div>
                    </div>
                </ItemTemplate>
            </asp:Repeater>
          </div>                                 
                       
       </div>
    </div>

    <div id="CheckInpopup" runat="server" class="popupwZc ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label23" runat="server">Today’s Check-ins</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton7" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table8">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST NAME</th>
                                          <th scope="col">CONTACT</th>
                                         <%-- <th scope="col">EMAIL</th>
                                          <th scope="col">CNIC/PASSPORT</th>--%>
                                          <th scope="col">ADULTS</th>
                                          <th scope="col">KIDS</th>
                                          <th scope="col">TOTAL</th>
                                          <th scope="col">ROOMS</th>
                                          <th scope="col">RESERVATION</th>
                                          <th scope="col">ARRIVAL </th>
                                          <th scope="col">DEPARTURE </th>
                                          
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="CheckInrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("FullName")%></td>
                                                    <td><%# Eval("PhoneNo")%></td>
                                                   <%-- <td><%# Eval("Email")%></td>   
                                                    <td><%# Eval("CNIC") +" / "+ Eval("VisaPassportNo")%></td>--%>
                                                    <td style="text-align:right"><%# Eval("adults")%></td>
                                                    <td style="text-align:right"><%# Eval("minors")%></td>
                                                    <td style="text-align:right"><%# Convert.ToInt32(Eval("adults")) + Convert.ToInt32(Eval("minors")) %></td>
                                                    <td style="text-align:right"><%# Eval("TotalRooms") %></td>
                                                    <td><%# Eval("reg_id")%></td>
                                                    <td><%# Eval("ArrivalDate")%></td> 
                                                    <td><%# Eval("DepartureDate")%></td> 
                                                           
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                                <tr>
                                                    <td></td><td></td><td><b style="margin=0px; padding:0px;">Total</b></td>
                                                    <td style="text-align:right"><asp:Label runat="server" fontsize="14px" ID="adults"></asp:Label></td>
                                                    <td style="text-align:right"><asp:Label runat="server" fontsize="14px" ID="kids"></asp:Label></td>
                                                    <td style="text-align:right"><asp:Label runat="server" fontsize="14px" ID="total"></asp:Label></td>
                                                    <td style="text-align:right"><asp:Label runat="server" fontsize="14px" ID="booking"></asp:Label></td>
                                                    <td></td><td></td><td></td> 
                                                </tr>
                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="Dirtyrooompopup" runat="server" class="popupwZ ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label24" runat="server">DIRTY ROOMS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton8" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                       <div class="repeaterheight2" style="overflow:auto;" >
            <asp:Repeater ID="Dirtyrooomrepeater" runat="server" OnItemDataBound="CategoryRepeater_ItemDataBound">
                <ItemTemplate>
                    <div class="category">
                       <b>
                       <img src="img/icons8-double-bed-30.png" style="height:28px; width:20px;" />
                       <asp:Label runat="server" id="lblMessage" style=" text-transform:uppercase;   border-bottom:1px solid black; font-size: 15px; color: #545454;"><%# Eval("RoomCategory") %></asp:Label></b> 

                        <div class="d-flex" style="padding:10px; overflow:auto;">
                        <asp:Repeater ID="RoomRepeater" runat="server">
                            <HeaderTemplate>
                                <table style="border:0px solid black;">
                                    <tr style="border:0px solid black;">
                            </HeaderTemplate>

                            <ItemTemplate>
                                <%# (Container.ItemIndex != 0 && Container.ItemIndex % 10 == 0) ? @"</tr><tr style='border: 0px;'>" : string.Empty %>
                             
                                    <div class="col-md-1 item" id="divRoom" style="background-color:#ffffff; border: 1px solid lightgray;
                                          border-radius: 15px; margin-bottom: 3px;  padding: 10px; box-shadow: 0px 0px 10px 2px rgba(0, 0, 0, 0.1);">
                                          <asp:Label font-size="12px" runat="server" id="label" text='<%# Eval("RoomNumber") %>' font-bold="true" forecolor="#333"></asp:Label><br />
                                     </div>
                               
                            </ItemTemplate>

                            <FooterTemplate>
                                    </tr>
                                    
                                </table>
                            </FooterTemplate>
                        </asp:Repeater>
                        </div>
                    </div>
                </ItemTemplate>
            </asp:Repeater>
          </div> 
                       
                       
       </div>
    </div>

    <div id="Pendingspopup" runat="server" class="popupwZc ora-compact-detail-popup">
             <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label25" runat="server">PENDING CHECK-IN(S)</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton9" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                            <div class="repeaterheight"  style=" overflow: auto;">
                                 <table id="Table10">
                                        <thead style="position: sticky; top: 0; z-index: 1; background-color: #fff;">
                                            <tr style="font-size:12px;">                      
                                              <th style=" padding-left:5px;" scope="col">Sr # </th>
                                             
                                              <th>GUEST NAME</th>
                                              <th>CONTACT</th>
                                              <%--<th>EMAIL</th>--%>
                                              <th>ARRIVAL</th>
                                              <th>ADDRESS</th>
                                              <th>SOURCE</th> 
                                              
                                              <th>PAY METHOD</th> 
                                              <th>ADVANCE</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                              <asp:Repeater ID="PendingsRepeater" runat="server">
                                                 <ItemTemplate>
                                                   <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                       <td style=" padding-left:5px;"><%# Container.ItemIndex + 1 %></td>
                                                       
                                                       <td><%# Eval("GuestName") + " " + Eval("LastName") %></td>
                                                       <td><%# Eval("PhoneNo")%></td>
                                                      <%-- <td ><%# Eval("Email")%></td>--%>
                                                       <td ><%# Eval("ArrivalDate")%></td>
                                                       <td style=" white-space:normal;"><%# Eval("Address") %></td>
                                                          
                                                       <td><%# Eval("Agency") %></td>
                                                       <td ><%# Eval("payment_method") %></td>
                                                       <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("advance_paid") %></td>
                                                       
                                                    </tr>
                                                 </ItemTemplate>
                                              </asp:Repeater>
                                          </tbody>
                                          <tr>
                                              <td></td><td></td><td></td>
                                              <td></td><td></td><td></td>
                                              <td style="font-weight:bold; padding:5px;"> Total:</td>
                                              <td style="text-align:right; padding:5px;"><asp:Label runat="server" ID="totaladvancereservationpaid" Text="0"></asp:Label></td>
                                              
                                          </tr>
                                 </table>
                            </div>    
             </div>
    </div>

    <div id="NOshowpopup" runat="server" class="popupwZc ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label26" runat="server">No Show Details</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton10" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style=" overflow: auto;">
                             <table id="Table11">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST NAME</th>
                                           <th scope="col">EMAIL</th>
                                          <th scope="col">ARRIVAL DATE</th>
                                          <th> ADDRESS</th>
                                          <th scope="col">CONTACT</th>
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="NoShowrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("GuestName")+" "+ Eval("LastName")%></td>
                                                    <td><%# Eval("Email")%></td>
                                                    <td><%# Eval("ArrivalDate")%></td>
                                                    <td><%# Eval("Address")%></td>  
                                                    <td><%# Eval("PhoneNo")%></td> 
                                                                 
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="Revenuepopup" runat="server" class="popupwZc ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label27" runat="server">REVENUE RECEIVED</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton11" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style=" overflow: auto;">
                             <table id="Table12">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST NAME</th>
                                          <th scope="col">VISIT ID</th>
                                          <th scope="col">USERNAME</th>
                                          <th scope="col">SHIFT</th>
                                          <th scope="col">DATE</th>
                                          <th scope="col">PAID AMOUNT</th>
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="Revenuerepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("name")%></td>
                                                    <td><%# Eval("newvisit_id")%></td> 
                                                    <td><%# Eval("username")%></td> 
                                                    <td><%# Eval("shift")%></td> 
                                                    <td><%# Eval("currentdate")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("paid_amount")%></td>
                                                            
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                          <tr>
                                              <td></td><td></td> <td></td><td></td><td></td><td></td>
                                              <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalrevenue" Text="0"></asp:Label></td>
                                             
                                          </tr>
                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="Pendingpaypopup" runat="server" class="popupwZ ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label28" runat="server">PENDING REVENUE</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton12" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight2"  style="overflow: auto;">
                             <table id="Table13">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">Registration# </th>
                                          <th scope="col">GUEST NAME</th>
                                          <th>ARRIVAL DATE</th>
                                          <th>DEPARTURE DATE</th>
                                          <th style="display:none;"> INVOICE</th>
                                          <th scope="col">REMAINING AMOUNT</th>
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="Pendingpayrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("reg_id")%></td>
                                                    <td><%# Eval("guest_name")%></td>
                                                    <td><%# Eval("arr_date") == null ? "" : string.Format("{0:dd/MM/yyyy}", Eval("arr_date")) %></td>
<td><%# Eval("dept_date") == null ? "" : string.Format("{0:dd/MM/yyyy}", Eval("dept_date")) %></td>

                                                    <td style="display:none;"> 
                                                          <asp:LinkButton OnClick="OpenInvoice" Text='<%# Eval("reg_id") %>' ID="btn_showInvoice" runat="server" CommandArgument='<%# Eval("reg_id")%>'></asp:LinkButton>
                                                    </td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("remaining_amount")%></td>
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                          
                                          <tr>
                                          <td></td><td></td><td></td><td></td><td></td>
                                              <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalremainingamount" Text="0"></asp:Label></td>
                                             </tr>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    
    <div id="Cancellationpopup" runat="server" class="popupwZc ora-compact-detail-popup ora-cancellation-ledger-popup">
        <div style="margin:0;">
            <div class="popupheaderforpopup">
                <div>
                    <asp:Label CssClass="headertext" ID="Label29" runat="server">
                        Cancellation Details
                    </asp:Label>
                    <div class="ora-popup-date-range">
                        Selected arrival dates:
                        <asp:Label ID="lblCancellationDateRange" runat="server" />
                    </div>
                </div>

                <asp:ImageButton runat="server"
                    ID="ImageButton13"
                    OnClick="closepopup"
                    Height="30px"
                    Width="30px"
                    ImageUrl="img/icons8-cross-50.png"
                    CausesValidation="false" />
            </div>

            <div class="repeaterheight ora-cancellation-table-wrap">
                <table id="Table14" class="ora-cancellation-ledger-table">
                    <thead>
                        <tr>
                            <th>Sr#</th>
                            <th>Reservation#</th>
                            <th>Room No</th>
                            <th>Guest/Booker</th>
                            <th>Company</th>
                            <th>No. of Nights</th>
                            <th>Rate Plan</th>
                            <th>Arrival Date</th>
                            <th>Cancellation Date</th>
                            <th class="text-right">Total</th>
                            <th class="text-right">Paid</th>
                            <th class="text-right">Remaining</th>
                            <th>Cancelled By</th>
                            <th>Notes</th>
                        </tr>
                    </thead>

                    <tbody>
                        <asp:Repeater ID="CancellationRepeater" runat="server">
                            <ItemTemplate>
                                <tr class="ora-cancellation-main-row">
                                    <td><%# Container.ItemIndex + 1 %></td>

                                    <td>
                                        <%# DashboardGetText(Container.DataItem,
                                            "reg_id", "RegistrationNo", "ReservationNo",
                                            "reservation_no", "ReservationId") %>
                                    </td>

                                    <td>
                                        <span class="ora-room-number">
                                            <%# DashboardGetText(Container.DataItem,
                                                "room_no", "RoomNo", "room_nos",
                                                "RoomNumber") %>
                                        </span>
                                    </td>

                                    <td>
                                        <%# DashboardGetGuestName(Container.DataItem) %>
                                    </td>

                                    <td>
                                        <%# DashboardGetText(Container.DataItem,
                                            "Company", "company", "booking_medium",
                                            "BookingMedium", "BookingSource",
                                            "booking_source", "source") %>
                                    </td>

                                    <td>
                                        <%# DashboardGetText(Container.DataItem,
                                            "nights", "NoOfNights", "no_of_nights",
                                            "Night", "NightCount") %>
                                    </td>

                                    <td>
                                        <%# DashboardGetText(Container.DataItem,
                                            "rateplan_name", "rateplan_names",
                                            "RatePlan", "rate_plan", "PlanName") %>
                                    </td>

                                    <td>
                                        <%# DashboardFormatFlexibleDate(
                                            DashboardGetValue(Container.DataItem,
                                                "ArrivalDate", "arrival_date",
                                                "arr_date", "Arrival")) %>
                                    </td>

                                    <td>
                                        <%# DashboardFormatFlexibleDateTime(
                                            DashboardGetValue(Container.DataItem,
                                                "CancellationDate", "cancellation_date",
                                                "CancelledDate", "cancelled_date",
                                                "currentdate", "CreatedAt")) %>
                                    </td>

                                    <td class="text-right">
                                        <%# DashboardFormatCurrency(
                                            DashboardGetValue(Container.DataItem,
                                                "totalamount", "TotalAmount",
                                                "total", "Total", "booking_value")) %>
                                    </td>

                                    <td class="text-right">
                                        <%# DashboardFormatCurrency(
                                            DashboardGetValue(Container.DataItem,
                                                "paid_amount", "PaidAmount",
                                                "paid", "Paid", "amount_paid")) %>
                                    </td>

                                    <td class="text-right">
                                        <%# DashboardFormatCurrency(
                                            DashboardGetValue(Container.DataItem,
                                                "remaining_amount", "RemainingAmount",
                                                "remaining", "Remaining",
                                                "balance")) %>
                                    </td>

                                    <td>
                                        <%# DashboardGetText(Container.DataItem,
                                            "cancelled_by", "CancelledBy",
                                            "cancel_by", "username",
                                            "UserName", "created_by") %>
                                    </td>

                                    <td class="ora-cancellation-notes-cell">
                                        <%# DashboardGetText(Container.DataItem,
                                            "note", "notes", "Notes",
                                            "remarks", "Remarks",
                                            "reason", "Reason",
                                            "cancel_reason", "CancellationReason") %>
                                    </td>
                                </tr>


                            </ItemTemplate>
                        </asp:Repeater>
                    </tbody>
                </table>

                <asp:Panel ID="pnlCancellationEmpty"
                    runat="server"
                    Visible="false"
                    CssClass="ora-popup-empty">
                    No cancellation records found for the selected arrival dates.
                </asp:Panel>
            </div>
        </div>
    </div>


    <div id="Expenseshowpopup" runat="server" class="popupwZc ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label30" runat="server">PARTIALLY EXPENSES</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton14" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table15">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">CATEGORY</th>
                                          <th scope="col">DESCRIPTION</th>
                                          <th scope="col">EXPENSE TYPE</th>
                                          <th scope="col">DATE</th>
                                          <th scope="col">AMOUNT</th>
                                       </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="ExpenseshowRepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("category")%></td>
                                                    <td><%# Eval("description")%></td> 
                                                    <td><%# Eval("expense_type")%></td> 
                                                    <td><%# Eval("currentdate")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("amount")%></td>  
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                              <tr>
                                              <td></td><td></td><td></td><td></td><td></td>
                                              <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalexpenseamount" Text="0"></asp:Label></td>
                                             </tr>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="Purchasingpopupshow" runat="server" class="popupwZc">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label32" runat="server">PURCHASING INVENTORY ITEMS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton15" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table16">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                         <%-- <th scope="col">INVOICE NO</th>
                                          <th scope="col">SKU NO</th>--%>
                                           <th scope="col">SUPPLIER</th>
                                          <th scope="col">DATE</th>
                                          <th scope="col">ITEM</th>
                                           <th scope="col">QUANTITY</th> 
                                          <th scope="col">UNIT</th>                                    
                                          <th scope="col">RATE</th>
                                         
                                          <th scope="col">TOTAL BILL</th>
                                         
                                         
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="showPurchasingRepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px; cursor:pointer;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <%--<td style="text-align:right;"><%# Eval("invoiceno")%></td>
                                                    <td style="text-align:right;"><%# Eval("itemcode")%></td>--%>
                                                     <td><%# Eval("supplier")%></td> 
                                                    <td><%# Eval("currentdate")%></td> 
                                                    <td ><%# Eval("itemname")%></td>
                                                    <td style="text-align:right;"><%# Eval("quantity")%></td>
                                                    <td><%# Eval("unit")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("rate")%></td>
                                                    
                                                     <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("totalbill")%></td> 
                                                   
                                                   
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
<tr>
                                              <td></td><td></td><td></td><td></td><td></td><td></td><td></td>
                                              <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalpurchasesamount" Text="0"></asp:Label></td>
                                              
                                             </tr>
                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="Paymentpopshow" runat="server" class="popupwZc">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label33" runat="server">PAYMENTS TO SUPPLIER</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton16" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table17">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">INVOICE NO</th>
                                          <th scope="col">SKU NO</th>
                                          <th scope="col">ITEM</th>                                     
                                          <th scope="col">RATE</th>
                                          <th scope="col">QUANTITY</th>
                                          <th scope="col">UNIT</th>
                                          <th scope="col">TOTAL BILL</th>
                                          <th scope="col">SUPPLIER</th>
                                          <th scope="col">DATE</th>
                                         
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="Paymentpopshow1" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td style="text-align:right;"><%# Eval("invoiceno")%></td>
                                                    <td style="text-align:right;"><%# Eval("itemcode")%></td>
                                                    <td><%# Eval("itemname")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("rate")%></td>
                                                    <td style="text-align:right;"><%# Eval("quantity")%></td>
                                                    <td><%# Eval("unit")%></td> 
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("totalbill")%></td> 
                                                    <td><%# Eval("supplier")%></td> 
                                                    <td><%# Eval("currentdate")%></td> 
                                                   
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="roomsecuritypopshow" runat="server" class="popupwZc ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label34" runat="server">PAYABLE ROOM SECURITY</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton17" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table18">
                                    <thead>
                                        <tr style=" text-align:left;">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST</th>
                                          <th scope="col">PHONE NO</th>
                                        <%--  <th>VISIT ID</th>--%>
                                          <th scope="col">ROOM SECURITY</th>
                                          
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="roomsecurityrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("name")%></td>
                                                    <td><%# Eval("PhoneNo")%></td>
                                                   <%-- <td><%# Eval("visit_id")%></td>--%>
                                                   <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("security")%></td> 
                                                    
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                                <tr>
                                                    <td></td>
                                                    <td></td>
                                                    <td></td>
                                                  <%--  <td></td>--%>
                                                    <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalsecurityamount" Text="0"></asp:Label></td>
                                                </tr>
                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="PAYABLEAMOUNTPOPUP" runat="server" class="popupwZc">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label35" runat="server">INVOICE RECEIVED</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton18" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto;">
                             <table id="Table19">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">INVOICE NO</th>
                                          <th scope="col">SUPPLIER</th>
                                          <th scope="col">TOTAL BILL</th>
                                       </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="PAYABLEAMOUNTRepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("invoiceno")%></td>
                                                    <td><%# Eval("supplier")%></td> 
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("totalbill")%></td> 
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                          <tr>
                                                <td></td><td></td><td></td>
                                                <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalinvoicebill" Text="0"></asp:Label></td>
                                               
                                          </tr>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="totalcustomerpopup" runat="server" class="popupwZcx ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label36" runat="server">CUSTOMERS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton19" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight"  style="overflow: auto; width:100%;">
                             <table id="Table20" style="width:100%;">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GENDER</th>
                                          <th scope="col">GUEST</th>
                                          <th scope="col">PHONE NO</th>
                                          <%--<th scope="col">EMAIL</th>  --%>                                 
                                          <th scope="col">ADDRESS</th>
                                          <th scope="col">AGENCY</th>
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="totalcustomerrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px; width:100%;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("gender")%></td>
                                                    <td><%# Eval("GuestName") + " " + Eval("LastName")%></td>
                                                    <td><%# Eval("PhoneNo")%></td>
                                                    <%--<td><%# Eval("Email")%></td>--%>
                                                    <td><%# Eval("Address")%></td>
                                                    <td><%# Eval("Agency")%></td> 
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
                       </div>
                    </div>

                    <div id="profitlosspopup" runat="server" class="popupwZcy ora-compact-detail-popup">
                       <div style="margin-bottom:15px; height:600px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label37" runat="server">PROFIT/LOSS</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton20" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                                <p style="text-align:center; font-weight:bold; color:White; background:black; margin:10px 0px; font-size:24px;">REVENUE</p>               
                            <div class="repeaterheightforproftloss"  style="overflow: auto;">
                             
                             <table id="Table21">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST NAME</th>
                                          <th scope="col">VISIT ID</th>
                                          <th scope="col">USERNAME</th>
                                          <th scope="col">SHIFT</th>
                                          <th scope="col">DATE</th>
                                          <th scope="col">PAID AMOUNT</th>
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="RRepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("name")%></td>
                                                    <td><%# Eval("visit_id")%></td> 
                                                    <td><%# Eval("username")%></td> 
                                                    <td><%# Eval("shift")%></td> 
                                                    <td><%# Eval("currentdate")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("paid_amount")%></td>
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>  
                            
                               <p  style="text-align:center; font-weight:bold; color:White; background:black; margin:10px 0px; font-size:24px;">EXPENSE</p> 
                               <div class="repeaterheightforproftloss"  style="overflow: auto;">
                              
                             <table id="Table22">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">DESCRIPTION</th>
                                          <th scope="col">CATEGORY</th>
                                          <th scope="col">EXPENSE TYPE</th>
                                          <th scope="col">DATE</th>
                                          <th scope="col">AMOUNT</th>
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="EREPEATER" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("description")%></td>
                                                    <td><%# Eval("category")%></td>
                                                    <td><%# Eval("expense_type")%></td> 
                                                    <td><%# Eval("currentdate")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("amount")%></td>
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>  
                            
                          <div class="row" style="margin:5px; Font-Size:18px; margin-bottom:5px;">
                             <div class="col-md-4 col-12" style="display:flex; justify-content:space-between;" > <p style="font-weight:bold; width:40%;">Revenue: </p>  <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="rev" Font-Size="20px" style="width:60%; "></asp:Label> </div>
                             <div class="col-md-4 col-12" style="display:flex; justify-content:space-between;" > <p style="font-weight:bold; width:40%;">Expense: </p> &nbsp  <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="exp" Font-Size="20px" style="width:60%; "></asp:Label>  </div>
                             <div class="col-md-4 col-12" style="display:flex; justify-content:space-between;" > <p style="font-weight:bold; width:40%;">Profit/Loss: </p> &nbsp  <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="PNL" Font-Size="20px" style="width:60%; "></asp:Label>  </div>
                          </div>   
       </div>
    </div>

    <div id="Staffpopup" runat="server" class="popupwZ ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label38" runat="server">STAFF</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton21" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                              <div class="repeaterheight2"  style="overflow: auto;">
                             <table id="Table23">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th>GENDER</th>
                                          <th scope="col">NAME</th>
                                          <th scope="col">PHONE</th>
                                          <th scope="col">EMAIL</th>
                                          <th scope="col">CITY</th>
                                          <th scope="col">DEPARTMENT</th>
                                          <th scope="col">DATE HIRED</th>
                                          <th>ATTENDANCE STATUS</th>
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="Staffrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("Gender")%></td>
                                                    <td><%# Eval("Firstname")%></td>
                                                    <td><%# Eval("Email")%></td>
                                                    <td><%# Eval("Phone")%></td>
                                                    <td><%# Eval("Department")%></td>
                                                    <td><%# Eval("Date_hired")%></td>            
                                                    <td><%# Eval("City")%></td> 
                                                    <td><%# Eval("AttendanceStatus")%></td>
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>

                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <div id="refundpopup" runat="server" class="popupwZ ora-compact-detail-popup">
       <div style="margin-bottom:15px;">
                            <div class="popupheaderforpopup">
                                <asp:Label class="headertext" ID="Label3" runat="server">REFUND</asp:Label>
                                <asp:ImageButton runat="server" ID="ImageButton1" OnClick="closepopup" hieght="15px" Width="35px" src="img/icons8-cross-50.png"/>    
                            </div>
                                              
                            <div class="repeaterheight2"  style="overflow: auto;">
                             <table id="Table24">
                                    <thead>
                                        <tr style=" text-align:left; ">
                                          <th scope="col">SR# </th>
                                          <th scope="col">GUEST NAME</th>
                                          <th>ARRIVAL DATE</th>
                                          <th>DEPARTURE DATE</th>
                                          <th>VISIT ID</th>
                                           <th scope="col">DATE</th>
                                          <th scope="col">REFUNDED AMOUNT</th>
                                         
                                        </tr>
                                      </thead>
                                      <tbody>
                                        
                                          <asp:Repeater ID="refundrepeater" runat="server">
                                              <ItemTemplate>
                                                <tr class='<%# Container.ItemIndex % 2 == 0 ? "evenRow" : "oddRow" %>' style="text-align:left; font-size:10px;">
                                                    <td><%# Container.ItemIndex + 1 %></td>
                                                    <td><%# Eval("GuestName") + " " + Eval("LastName")%></td>
                                                    <td><%# Eval("ArrivalDate")%></td>
                                                    <td><%# Eval("DepartureDate")%></td>
                                                    <td><%# Eval("visit_id")%></td>
                                                     <td><%# Eval("currentdate")%></td>
                                                    <td style="text-align:right;"> <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><%# Eval("Refund")%></td>
                                                   
                                                </tr>
                                              </ItemTemplate>
                                          </asp:Repeater>
                                                <tr>
                                                    <td></td><td></td><td></td><td></td><td></td><td></td>
                                                    <td style="text-align:right; padding:5px;"><b>Total:</b>&nbsp <span style="font-size:15px;"><%= Session["currency_symbol"] %></span><asp:Label runat="server" ID="totalrefundamount" Text="0"></asp:Label></td>
                                                    
                                                </tr>
                                      </tbody>
                                </table>
                            </div>    
       </div>
    </div>

    <script type="text/javascript">
        function rowClicked1(row) {
            var invoiceNo = row.cells[1].innerText;
            var HiddenField10 = document.getElementById('<%= HiddenField1.ClientID %>');
            HiddenField10.value = invoiceNo;
            
            
            triggerPostBack1();
        }

        function triggerPostBack1() {
            var btnshow = document.getElementById('<%= Button1.ClientID %>');
            if (btnshow) {
                btnshow.click();
            }
        }
    </script>
    <script type="text/javascript">
        function rowClicked2(row) {
            var roomno = row.cells[1].innerText;
            var status = row.cells[3].innerText;
            var HiddenField11 = document.getElementById('<%= HiddenField4.ClientID %>');
            HiddenField11.value = roomno;
            var HiddenField12 = document.getElementById('<%= HiddenField5.ClientID %>');
            HiddenField12.value = status;

            triggerPostBack2();
        }

        function triggerPostBack2() {

            var btnshow = document.getElementById('<%= Button2.ClientID %>');
            if (btnshow) {
                btnshow.click();
            }
        }
    </script>

<asp:Button ID="AvailableHB" runat="server" style="display: none;" OnClick="HiddenButton_Available" />
<asp:Button ID="totalHB" runat="server" style="display: none;" OnClick="HiddenButton_totalroom" />
<asp:Button ID="blockedHB" runat="server" style="display: none;" OnClick="HiddenButton_blockedroom" />
<asp:Button ID="OccupancyHB" runat="server" style="display: none;" OnClick="HiddenButton_Occupancy" />
<input type="hidden" id="operationPopupMode" name="operationPopupMode" value="checkin" />
<asp:Button ID="CheckInHB" runat="server" style="display: none;" OnClick="HiddenButton_CheckIn" />
<asp:Button ID="currentCheckInHB" runat="server" style="display: none;" OnClick="HiddenButton_currentCheckIn" />
<asp:Button ID="DirtyRoomsHB" runat="server" style="display: none;" OnClick="HiddenButton_DirtyRooms" />
<asp:Button ID="PendingsHB" runat="server" style="display: none;" OnClick="HiddenButton_Pendings" />
<asp:Button ID="NoShowHB" runat="server" style="display: none;" OnClick="HiddenButton_NoShow" />
<asp:Button ID="RevenueHB" runat="server" style="display: none;" OnClick="HiddenButton_Revenue" />
<asp:Button ID="PendingPayHB" runat="server" style="display: none;" OnClick="HiddenButton_PendingPay" />
<asp:Button ID="CancellationHB" runat="server" style="display: none;" OnClick="HiddenButton_Cancellation" />
<asp:Button ID="ExpenseHB" runat="server" style="display: none;" OnClick="HiddenButton_Expense" />
<asp:Button ID="PurchasingHB" runat="server" style="display: none;" OnClick="HiddenButton_Purchases" />
<asp:Button ID="paymentsHB" runat="server" style="display: none;" OnClick="HiddenButton_payments" />
<asp:Button ID="roomsecurityHB" runat="server" style="display: none;" OnClick="HiddenButton_roomsecurity" />
<asp:Button ID="PAYABLEAMOUNTSHB" runat="server" style="display: none;" OnClick="HiddenButton_PAYABLEAMOUNT" />
<asp:Button ID="TotalCustomerHB" runat="server" style="display: none;" OnClick="HiddenButton_TotalCustomer" />
<asp:Button ID="ProfitLossHB" runat="server" style="display: none;" OnClick="HiddenButton_ProfitLoss" />
<asp:Button ID="staffHB" runat="server" style="display: none;" OnClick="HiddenButton_STAFF" />
<asp:Button ID="refundHB" runat="server" style="display: none;" OnClick="HiddenButton_Refund" />
<asp:Button ID="propertybutn" runat="server" style="display: none;" OnClick="HiddenButton_gotoproperty" />

<script>
     function showAvailable() {
         document.getElementById('<%= AvailableHB.ClientID %>').click();
     }
     function showTotal() {
        document.getElementById('<%= totalHB.ClientID %>').click();
     }
     function showblocked() {
        document.getElementById('<%= blockedHB.ClientID %>').click();
     }
     function showOccupancy() {
         document.getElementById('<%= OccupancyHB.ClientID %>').click();
     }
     function showCheckin() {
         var mode = document.getElementById('operationPopupMode');
         if (mode) mode.value = 'checkin';
         document.getElementById('<%= CheckInHB.ClientID %>').click();
     }

     function showCheckout() {
         var mode = document.getElementById('operationPopupMode');
         if (mode) mode.value = 'checkout';
         document.getElementById('<%= CheckInHB.ClientID %>').click();
     }
     function showcurrentCheckin() {
        document.getElementById('<%= currentCheckInHB.ClientID %>').click();
     }
     function gotopropertypage() {
         document.getElementById('<%= propertybutn.ClientID %>').click();
     }
     function showDirtyRooms() {
         document.getElementById('<%= DirtyRoomsHB.ClientID %>').click();
     }
     function showPendings() {
         document.getElementById('<%= PendingsHB.ClientID %>').click();
     }
     function showNOsHOW() {
         document.getElementById('<%= NoShowHB.ClientID %>').click();
     }
     function showrevenue() {
         document.getElementById('<%= RevenueHB.ClientID %>').click();
     }
     function showPendingPay() {
         document.getElementById('<%= PendingPayHB.ClientID %>').click();
     }
     function showCancellation() {
         document.getElementById('<%= CancellationHB.ClientID %>').click();
     }
     function showExpenses() {
         document.getElementById('<%= ExpenseHB.ClientID %>').click();
     }
     function showPurchases() {
         document.getElementById('<%= PurchasingHB.ClientID %>').click();
     }
     function showpaymentsrecord() {
         document.getElementById('<%= paymentsHB.ClientID %>').click();
     }
    function showroomsecurity() {
        // Get the textbox element by its ClientID
        var txt = document.getElementById('<%= Payableroomsecuritytxt.ClientID %>');
    
    // Check if it exists and its value is not 0 or empty
    if (txt && txt.value && parseFloat(txt.value) !== 0) {
            document.getElementById('<%= roomsecurityHB.ClientID %>').click();
        }
    }

     function SHOWPAYABLEAMOUNTHB() {
         document.getElementById('<%= PAYABLEAMOUNTSHB.ClientID %>').click();
     }
     function SHOWTotalCustomer() {
         document.getElementById('<%= TotalCustomerHB.ClientID %>').click();
     }
     function SHOWProfitLoss() {
         document.getElementById('<%= ProfitLossHB.ClientID %>').click();
     }
     function SHOWSTAFF() {
         document.getElementById('<%= staffHB.ClientID %>').click();
     }
     function showrefund() {
         document.getElementById('<%= refundHB.ClientID %>').click();
    }


</script>
    </ContentTemplate>
</asp:UpdatePanel>


<script type="text/javascript">
    /* Premium popup safety helper: preserves all existing popup open/close functions.
       Adds Escape key close + overlay click close only when matching elements are visible. */
    (function () {
        function hideVisibleDashboardPopups() {
            var popups = document.querySelectorAll('.popupwZcx,.popupwZ,.popupwZc,.popupwZcy,.popupw');
            var closedAny = false;

            popups.forEach(function (popup) {
                var style = window.getComputedStyle(popup);
                if (style.display !== 'none' && style.visibility !== 'hidden') {
                    popup.style.display = 'none';
                    closedAny = true;
                }
            });

            var overlays = document.querySelectorAll('.screenblur');
            overlays.forEach(function (overlay) {
                overlay.style.display = 'none';
            });

            return closedAny;
        }

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                hideVisibleDashboardPopups();
            }
        });

        document.addEventListener('click', function (e) {
            if (e.target && e.target.classList && e.target.classList.contains('screenblur')) {
                hideVisibleDashboardPopups();
            }
        });
    })();
</script>


<!-- =========================================================
     LEGACY SERVER CONTROL COMPATIBILITY
     These controls remain instantiated for existing code-behind
     methods but are not shown in the redesigned dashboard UI.
========================================================= -->
<div id="oraLegacyCompatibilityControls" style="display:none;" aria-hidden="true">
    <div runat="server" id="warningshow">
        <asp:Label runat="server" ID="warningmessage" />
    </div>

    <div runat="server" id="vatcard">
        <asp:Label runat="server" ID="taxlabel" />
        <asp:Label runat="server" ID="gsttax1" />
    </div>

    <div runat="server" id="bedcard">
        <asp:Label runat="server" ID="bedtax1" />
    </div>

    <asp:Label runat="server" ID="ratingavg" />
    <asp:Label runat="server" ID="txtfeedback" />
    <asp:Repeater runat="server" ID="feedbackrepeater" />

    <p runat="server" id="lvlRoomOccupancy">0%</p>
    <span runat="server" id="lvlRoomSold">0</span>
    <span runat="server" id="lvlRoomAvailable">0</span>

    <p runat="server" id="lvlPersonOccupancy">0%</p>
    <span runat="server" id="lvlPersonSold">0</span>
    <span runat="server" id="lvlPersonAvailable">0</span>

    <p runat="server" id="lvlavragerate">0</p>

    <asp:Repeater runat="server" ID="profitlossrepeater" />

    <asp:Label runat="server" ID="totalstaff" />

    <asp:Repeater runat="server" ID="childpropertyRepeater" />
    <asp:Label runat="server" ID="childheader" />
    <asp:Label runat="server" ID="childmsg" />

    <asp:UpdatePanel runat="server" ID="purchasingrepeaterupdatepanel" UpdateMode="Conditional">
        <ContentTemplate>
            <div runat="server" id="PurchasingDiv">
                <asp:Repeater runat="server" ID="purchasingrepeater" />
                <asp:Label runat="server" ID="purchasesrecord" />
            </div>
        </ContentTemplate>
    </asp:UpdatePanel>

    <asp:HiddenField runat="server" ID="hd_invoice" />

    <asp:Repeater runat="server" ID="DemandRepeater" />
    <div runat="server" id="DemandDiv">
        <asp:Label runat="server" ID="demandrecord" />
    </div>

    <asp:Label runat="server" ID="lbl_reservationList" />
    <div runat="server" id="tb_reservationlist">
        <asp:Repeater runat="server" ID="RepeaterGuestInformation" />
    </div>

    <asp:Label runat="server" ID="Label1" />
    <div runat="server" id="Div1">
        <asp:Repeater runat="server" ID="RepeaterNoshow" />
    </div>
</div>

</asp:Content> 
          
