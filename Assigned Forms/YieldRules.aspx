<%@ Page MasterPageFile="Site.Master" EnableEventValidation="false" Language="C#" AutoEventWireup="true"
    Inherits="hotelsoftware.YieldRules" Codebehind="YieldRules.aspx.cs" %>

<asp:Content ID="Content1" ContentPlaceHolderID="head" runat="server">
    <link href="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/css/select2.min.css" rel="stylesheet" />
    <link href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css" rel="stylesheet" />

   <style>
  :root{
    --brand:#10b7b0;
    --brand2:#0ea5a0;
    --brandDark:#0a7f7a;
    --ink:#0f172a;
    --muted:#64748b;
    --border:#e5e7eb;
    --bg:#f4f6fb;
    --card:#ffffff;
    --soft:#f8fafc;
    --danger:#e11d48;
    --success:#16a34a;
    --primary:#1d3557;
    --shadow:0 18px 50px rgba(2,6,23,.08);
    --shadow2:0 12px 24px rgba(2,6,23,.10);
    --ring:0 0 0 4px rgba(16,183,176,.14);
  }

  body{
    background:var(--bg) !important;
    font-family:'Montserrat', sans-serif;
  }

  .yield-page-shell{
    max-width:1320px;
    margin:0 auto;
  }

  .yield-page-head{
    background:#fff;
    border:1px solid rgba(15,23,42,.08);
    border-radius:22px;
    padding:3px 20px;
  }

  .yield-title-row{
    display:flex;
    justify-content:space-between;
    align-items:center;
    gap:14px;
    flex-wrap:wrap;
    margin-bottom:14px;
  }

  .yield-title-left{
    display:flex;
    align-items:center;
    gap:10px;
    font-size:20px;
    font-weight:900;
    color:#102a43;
    letter-spacing:.2px;
  }

  .yield-title-left .title-icon{
    width:42px;
    height:42px;
    display:inline-flex;
    align-items:center;
    justify-content:center;
    border-radius:14px;
    color:#fff;
    background:linear-gradient(135deg,#1d3557,#10b7b0);
    box-shadow:0 10px 22px rgba(16,183,176,.22);
  }

  .yield-subtitle{
    color:var(--muted);
    font-size:13px;
    font-weight:600;
    margin-top:3px;
  }

  .yield-topbar{
    background:linear-gradient(180deg,#ffffff 0%,#fbfdff 100%);
    border:1px solid rgba(15,23,42,.08);
    border-radius:18px;
    padding:14px;
    box-shadow:var(--shadow);
    margin-bottom:14px;
    position:sticky;
    top:8px;
    z-index:5;
  }

  .yield-search{
    max-width:520px;
    width:100%;
  }

  .yield-search .form-control{
    border-radius:0px !important;
    border:1px solid rgba(15,23,42,.10);
    height:44px;
    padding-left:16px;
    box-shadow:none;
    background:#fff;
  }

  .yield-search .form-control:focus{
    border-color:rgba(16,183,176,.60);
    box-shadow:var(--ring);
  }

  a.btn-teal,
  .btn-teal{
    display:inline-flex !important;
    align-items:center !important;
    justify-content:center !important;
    gap:8px;
    height:44px;
    background:#1d3557;
    color:#fff !important;
    border:0 !important;
    border-radius:0px !important;
    padding:0 18px !important;
    line-height:1 !important;
    vertical-align:middle;
    font-weight:800;
    box-shadow:0 10px 22px rgba(29,53,87,.15);
  }

  .yield-search .btn-teal{
    border-radius:0px !important;
    min-width:54px;
  }

  a.btn-teal:hover,
  .btn-teal:hover{
    background:#142944;
    transform:translateY(-1px);
  }

  .btn-help{
    border-radius:999px;
    height:44px;
    padding:0 16px;
    border:1px solid rgba(15,23,42,.10);
    background:#fff;
    font-weight:900;
    color:var(--ink);
  }

  .yield-card{
    background:var(--card);
    border:1px solid rgba(15,23,42,.08);
    border-radius:18px;
    box-shadow:var(--shadow2);
    overflow:hidden;
  }

  .table-responsive-yield{
    width:100%;
    overflow-x:auto;
  }

  .table{
    margin-bottom:0;
    background:#fff;
  }

  .table thead th{
    background:linear-gradient(180deg,#eff6ff 0%,#ecfeff 100%);
    border-top:0 !important;
    border-bottom:1px solid rgba(15,23,42,.08) !important;
    color:#083b39;
    font-weight:900;
    font-size:12px;
    text-transform:uppercase;
    letter-spacing:.6px;
    position:sticky;
    top:0;
    z-index:2;
    padding:13px 12px;
    white-space:nowrap;
  }

  .table td{
    vertical-align:middle;
    font-size:13px;
    color:var(--ink);
    padding:12px;
    white-space:nowrap;
  }

  .table tbody tr{ transition:background .12s ease; }
  .table tbody tr:hover{ background:#f2fffe; }

  .table a{
    color:#0a45a6;
    font-weight:900;
    text-decoration:none;
  }
  .table a:hover{ text-decoration:underline; }

  .grid-actions{
    display:flex;
    gap:8px;
    justify-content:flex-start;
  }

  .grid-actions a{
    display:inline-flex;
    width:34px;
    height:34px;
    align-items:center;
    justify-content:center;
    border-radius:12px;
    border:1px solid rgba(15,23,42,.10);
    background:#fff;
    transition:transform .08s ease, background .12s ease, box-shadow .12s ease;
    box-shadow:0 6px 16px rgba(2,6,23,.06);
  }

  .grid-actions a:hover{
    transform:translateY(-1px);
    background:#f8fafc;
    box-shadow:0 10px 20px rgba(2,6,23,.10);
  }

  .modal-dialog{
    max-width:980px;
  }

  .modal-content{
    border-radius:22px;
    border:1px solid rgba(15,23,42,.10);
    box-shadow:0 28px 80px rgba(2,6,23,.25);
    overflow:hidden;
  }

  .modal-header{
    background:linear-gradient(90deg,#eff6ff,#ecfeff);
    border-bottom:1px solid rgba(15,23,42,.08);
    padding:15px 18px;
    align-items:center;
  }

  .modal-title{
    font-weight:950;
    color:var(--ink);
    letter-spacing:.2px;
  }

  .modal-body{
    background:#fff;
    padding:18px;
  }

  .modal-footer{
    border-top:1px solid rgba(15,23,42,.08);
    padding:13px 18px;
    background:#fbfcfe;
  }

  .section-card{
    border:1px solid rgba(15,23,42,.08);
    border-radius:18px;
    background:#fff;
    padding:14px;
    margin-bottom:14px;
    box-shadow:0 8px 24px rgba(15,23,42,.04);
  }

  .section-title{
    display:flex;
    align-items:center;
    gap:8px;
    font-size:13px;
    font-weight:950;
    color:#0f172a;
    text-transform:uppercase;
    letter-spacing:.45px;
    margin-bottom:12px;
  }

  .field-label,
  .modal-body label{
    font-weight:900;
    color:#334155;
    font-size:13px;
    margin-bottom:6px;
  }

  .modal-body .form-control,
  .modal-body select{
    height:44px;
    border-radius:12px;
    border:1px solid rgba(15,23,42,.10);
    box-shadow:none;
    background:#fff;
  }

  .modal-body .form-control:focus,
  .modal-body select:focus{
    border-color:rgba(16,183,176,.60);
    box-shadow:var(--ring);
  }

  .modal-body .form-group{ margin-bottom:12px; }

  .selector-row{
    display:flex;
    gap:12px;
    align-items:flex-start;
    margin-bottom:12px;
  }

  .selector-wrap{ flex:1; min-width:0; }

  .selector-tools{
    display:flex;
    gap:8px;
    align-items:center;
    padding-top:26px;
  }

  .btn-ico{
    width:40px;
    height:40px;
    border-radius:12px;
    display:inline-flex;
    align-items:center;
    justify-content:center;
    border:0;
    box-shadow:0 8px 18px rgba(0,0,0,.08);
    cursor:pointer;
    transition:transform .12s ease, box-shadow .12s ease, opacity .12s ease;
  }

  .btn-ico:hover{
    transform:translateY(-1px);
    box-shadow:0 12px 22px rgba(0,0,0,.12);
    opacity:.95;
  }

  .btn-plus{ background:#16a34a; color:#fff; }
  .btn-x{ background:#e11d48; color:#fff; }

  .pill-row{
    display:flex;
    gap:10px;
    flex-wrap:wrap;
  }

  .pill-row label.btn{
    border-radius:999px;
    border:1px solid rgba(15,23,42,.10);
    background:#fff;
    font-weight:900;
    color:var(--muted);
    padding:8px 12px;
    box-shadow:0 6px 14px rgba(2,6,23,.06);
    display:flex;
    align-items:center;
    gap:8px;
  }

  .pill-row label.btn input{ transform:translateY(1px); }

  .threshold-box{
    border:1px solid rgba(15,23,42,.10);
    border-radius:16px;
    padding:14px;
    background:#f8fafc;
    color:#475569;
    font-weight:700;
  }

  .threshold-box .form-control{
    height:40px !important;
    border-radius:10px;
    margin:0 6px;
  }

  .select2-container{ width:100% !important; }

  .select2-container .select2-selection--multiple,
  .select2-container .select2-selection--single{
    min-height:46px !important;
    border-radius:12px !important;
    border:1px solid rgba(15,23,42,.12) !important;
    background:#fff !important;
  }

  .select2-container--default .select2-selection--multiple .select2-selection__rendered{
    padding:6px 10px !important;
  }

  .select2-container--default .select2-selection--multiple .select2-selection__choice{
    border:0 !important;
    background:#eef2ff !important;
    color:#334155 !important;
    border-radius:999px !important;
    padding:5px 10px !important;
    font-weight:800 !important;
    margin-top:5px !important;
  }

  .select2-container--default .select2-selection--multiple .select2-selection__choice__remove{
    border:0 !important;
    margin-right:6px !important;
  }

  .text-danger{ font-weight:900;color:red !important; }
  .text-success{ font-weight:950; }

  .form-row{
    display:flex;
    flex-wrap:wrap;
    margin-left:-7px;
    margin-right:-7px;
  }

  .form-row > .form-group{
    padding-left:7px;
    padding-right:7px;
  }

  .btn-save-yield{
    background:#16a34a !important;
    border:0 !important;
    border-radius:12px !important;
    font-weight:900 !important;
    padding:10px 22px !important;
    box-shadow:0 10px 22px rgba(22,163,74,.16);
  }

  .btn-cancel-yield,
  .btn-close-yield{
    border:0 !important;
    border-radius:12px !important;
    font-weight:800 !important;
    padding:10px 18px !important;
  }


  /* Requested: remove all border radius from Yield Rules page */
  .yield-page-shell *,
  .yield-page-shell *::before,
  .yield-page-shell *::after,
  #ruleModal *,
  #ruleModal *::before,
  #ruleModal *::after,
  .select2-container--default .select2-selection--multiple,
  .select2-container--default .select2-selection__choice,
  .select2-dropdown,
  .select2-results__option{
    border-radius:0 !important;
  }

  .sr-badge{
    display:inline-flex;
    align-items:center;
    justify-content:center;
    min-width:34px;
    height:28px;
    background:#eef2ff;
    border:1px solid #dbeafe;
    color:#1d3557;
    font-weight:900;
    font-size:12px;
  }

  .status-toggle{
    display:inline-flex !important;
    align-items:center;
    justify-content:space-between;
    gap:8px;
    width:104px;
    height:34px;
    padding:3px 8px !important;
    border:0 !important;
    color:#fff !important;
    text-decoration:none !important;
    font-weight:900 !important;
    font-size:11px !important;
    letter-spacing:.25px;
    box-shadow:0 8px 18px rgba(15,23,42,.10);
    transition:transform .12s ease, opacity .12s ease, box-shadow .12s ease;
  }

  .status-toggle:hover{
    transform:translateY(-1px);
    opacity:.94;
    color:#fff !important;
    text-decoration:none !important;
  }

  .status-toggle.is-active{ background:#16a34a; }
  .status-toggle.is-inactive{ background:#e11d48; }

  .status-toggle .toggle-dot{
    width:24px;
    height:24px;
    background:#fff;
    display:inline-flex;
    align-items:center;
    justify-content:center;
    color:#0f172a;
    font-size:10px;
    flex:0 0 24px;
  }

  .status-toggle.is-active .toggle-dot{ order:2; }
  .status-toggle.is-inactive .toggle-dot{ order:0; }


  @media (max-width:768px){
    .yield-topbar,
    .yield-title-row{
      flex-direction:column;
      gap:10px;
      align-items:stretch !important;
    }

    .yield-search{ max-width:100% !important; }

    .selector-row{
      flex-direction:column;
    }

    .selector-tools{
      padding-top:0;
      justify-content:flex-end;
      width:100%;
    }

    .grid-actions{ gap:6px; }
    .grid-actions a{ width:32px; height:32px; }
  }
  /* PROFESSIONAL MINI TOGGLE */

.mini-switch{
    display:inline-flex;
    align-items:center;
    justify-content:flex-start;
    width:46px;
    height:24px;
    padding:2px;
    border:0 !important;
    outline:none !important;
    text-decoration:none !important;
    transition:all .18s ease;
    cursor:pointer;
    position:relative;
    box-shadow:
        inset 0 1px 2px rgba(0,0,0,.12),
        0 2px 6px rgba(0,0,0,.08);
}

.mini-switch .switch-track{
    width:100%;
    height:100%;
    position:relative;
    display:block;
}

.mini-switch .switch-thumb{
    position:absolute;
    top:2px;
    left:2px;
    width:18px;
    height:18px;
    background:#fff;
    box-shadow:
        0 2px 6px rgba(0,0,0,.18);
    transition:all .18s ease;
}

/* ACTIVE */

.mini-switch.active{
    background:#16a34a !important;
}

.mini-switch.active .switch-thumb{
    left:24px;
}

/* INACTIVE */

.mini-switch.inactive{
    background:#cbd5e1 !important;
}

.mini-switch.inactive .switch-thumb{
    left:2px;
}

/* HOVER */

.mini-switch:hover{
    opacity:.92;
    transform:translateY(-1px);
}



  /* Beautiful confirmation popup for Yield Rule status change */
  #yieldStatusConfirmModal .modal-dialog{
    max-width:560px;
  }

  #yieldStatusConfirmModal .modal-content{
    border:0;
    border-radius:22px;
    overflow:hidden;
    box-shadow:0 34px 90px rgba(2,6,23,.34);
    background:#fff;
  }

  #yieldStatusConfirmModal .yield-confirm-head{
    background:linear-gradient(135deg,#1d3557 0%,#10b7b0 100%);
    color:#fff;
    padding:22px 22px 18px;
    position:relative;
  }

  #yieldStatusConfirmModal .yield-confirm-icon{
    width:58px;
    height:58px;
    border-radius:18px;
    display:flex;
    align-items:center;
    justify-content:center;
    background:rgba(255,255,255,.18);
    box-shadow:inset 0 0 0 1px rgba(255,255,255,.25);
    font-size:26px;
    margin-bottom:12px;
  }

  #yieldStatusConfirmModal .yield-confirm-title{
    font-size:20px;
    font-weight:950;
    margin:0;
    letter-spacing:.2px;
  }

  #yieldStatusConfirmModal .yield-confirm-subtitle{
    margin:6px 0 0;
    color:rgba(255,255,255,.88);
    font-size:13px;
    font-weight:700;
    line-height:1.45;
  }

  #yieldStatusConfirmModal .yield-confirm-body{
    padding:20px 22px;
  }

  #yieldStatusConfirmModal .yield-warning-box{
    border:1px solid rgba(245,158,11,.28);
    background:linear-gradient(180deg,#fffbeb,#fff7ed);
    color:#78350f;
    padding:13px 14px;
    display:flex;
    gap:11px;
    align-items:flex-start;
    font-size:13px;
    font-weight:800;
    line-height:1.5;
    border-radius:14px;
  }

  #yieldStatusConfirmModal .yield-info-list{
    margin:14px 0 0;
    padding:0;
    list-style:none;
  }

  #yieldStatusConfirmModal .yield-info-list li{
    display:flex;
    gap:9px;
    align-items:flex-start;
    color:#334155;
    font-size:13px;
    font-weight:700;
    margin-top:9px;
  }

  #yieldStatusConfirmModal .yield-info-list i{
    color:#10b7b0;
    margin-top:2px;
  }

  #yieldStatusConfirmModal .yield-confirm-footer{
    padding:14px 22px 20px;
    display:flex;
    justify-content:flex-end;
    gap:10px;
    background:#fbfdff;
    border-top:1px solid rgba(15,23,42,.08);
  }

  #yieldStatusConfirmModal .btn-yield-keep{
    border:1px solid rgba(15,23,42,.12);
    background:#fff;
    color:#334155;
    font-weight:normal;
    padding:10px 16px;
    border-radius:12px;
      font-size:13px !important;

  }

  #yieldStatusConfirmModal .btn-yield-confirm{
    border:0;
    background:#1d3557;
    color:#fff;
    font-weight:normal;
    padding:10px 17px;
    font-size:13px !important;
    border-radius:12px;
    box-shadow:0 12px 24px rgba(29,53,87,.18);
  }

  #yieldStatusConfirmModal .btn-yield-confirm:hover{
    background:#142944;
  }
 
</style>
</asp:Content>

<asp:Content ID="Content2" ContentPlaceHolderID="ContentPlaceHolder2" runat="server">
    <asp:HiddenField ID="hdHotelId" runat="server" />
    <asp:HiddenField ID="hdCreatedBy" runat="server" />
    <div class="yield-page-shell">
        <div class="yield-page-head">
            <div class="yield-title-row">
                <div class="yield-title-left">
                    <span class="title-icon"><i class="fa-solid fa-chart-line"></i></span>
                    <div>
                        <div>Yield Management Rules</div>
                        <div class="yield-subtitle">Create automatic rate increase/decrease rules by occupancy, room type and rate plan.</div>
                    </div>
                </div>
            </div>
        </div>

    <!-- TOP BAR -->
    <div class="yield-topbar d-flex justify-content-between align-items-center">
        <div class="yield-search input-group" style="max-width:420px;">
            <asp:TextBox ID="txtSearch" runat="server" CssClass="form-control" placeholder="Search..." />
            <div class="input-group-append">
                <asp:LinkButton ID="btnSearch" runat="server" CssClass="btn btn-teal" OnClick="btnSearch_Click">
                    <i class="fa fa-search"></i>
                </asp:LinkButton>
            </div>
        </div>

        <div class="d-flex align-items-center" style="gap:10px;">
            <asp:LinkButton ID="btnOpenAdd" runat="server" CssClass="btn btn-teal" OnClick="btnOpenAdd_Click">
                <i class="fa fa-plus"></i> Add
            </asp:LinkButton>
            <a class="btn btn-help d-none" href="javascript:void(0)"><i class="fa fa-circle-question"></i> Help</a>
        </div>
    </div>

    <asp:UpdatePanel ID="upList" runat="server" UpdateMode="Conditional">
        <ContentTemplate>
            <div class="yield-card">
                <div class="table-responsive-yield">
                <asp:GridView ID="gvRules" runat="server" CssClass="table table-bordered table-sm"
                    AutoGenerateColumns="false" DataKeyNames="ID"
                    OnRowCommand="gvRules_RowCommand" OnRowDataBound="gvRules_RowDataBound">
                    <Columns>

                        <asp:TemplateField HeaderText="Sr#">
                            <ItemTemplate>
                                <span class="sr-badge"><%# Container.DataItemIndex + 1 %></span>
                            </ItemTemplate>
                            <HeaderStyle Width="70" />
                            <ItemStyle Width="70" />
                        </asp:TemplateField>

                        <asp:TemplateField HeaderText="Name">
                            <ItemTemplate>
                                <asp:LinkButton ID="lnkEditName" runat="server" CommandName="editRule"
                                    CommandArgument='<%# Eval("ID") %>' Text='<%# Eval("RuleName") %>' />
                            </ItemTemplate>
                            <HeaderStyle Width="220" />
                        </asp:TemplateField>

                        <asp:BoundField HeaderText="Staying From" DataField="StayFromStr" />
                        <asp:BoundField HeaderText="Staying To" DataField="StayToStr" />
                        <asp:BoundField HeaderText="Occupancy (min)" DataField="ThresholdMinStr" />
                        <asp:BoundField HeaderText="Occupancy (max)" DataField="ThresholdMaxStr" />

                        <asp:TemplateField HeaderText="Change Type">
                            <ItemTemplate>
                                <asp:Literal ID="litChangeTypeIcon" runat="server"></asp:Literal>
                            </ItemTemplate>
                            <HeaderStyle Width="110" />
                        </asp:TemplateField>

                        <asp:BoundField HeaderText="Change Value" DataField="ChangeValue" />
                        <asp:BoundField HeaderText="Change Unit" DataField="ChangeUnitLabel" />
<asp:TemplateField HeaderText="Status">
    <ItemTemplate>
        <asp:LinkButton ID="btnToggleStatus"
            runat="server"
            CommandName="toggleStatus"
            CommandArgument='<%# Eval("ID") %>'
            CssClass='<%# Convert.ToBoolean(Eval("IsActive")) ? "mini-switch active" : "mini-switch inactive" %>'
            ToolTip='<%# Convert.ToBoolean(Eval("IsActive")) ? "Click to make inactive" : "Click to make active" %>'
            CausesValidation="false"
            OnClientClick='<%# "return showYieldStatusConfirm(this, " + (Convert.ToBoolean(Eval("IsActive")) ? "true" : "false") + ");" %>'>
            <span class="switch-thumb"></span>
        </asp:LinkButton>
    </ItemTemplate>
    <HeaderStyle Width="90" />
    <ItemStyle HorizontalAlign="Center" />
</asp:TemplateField>

                        <asp:TemplateField HeaderText="">
                            <ItemTemplate>
                                <div class="grid-actions">
                                    <asp:LinkButton ID="btnEdit" runat="server" CommandName="editRule"
                                        CommandArgument='<%# Eval("ID") %>' CssClass="text-primary">
                                        <i class="fa fa-pen-to-square"></i>
                                    </asp:LinkButton>

                                    <asp:LinkButton ID="btnDelete" runat="server" CommandName="deleteRule"
                                        CommandArgument='<%# Eval("ID") %>' CssClass="text-danger"
                                        OnClientClick="return confirm('Delete this rule?');">
                                        <i class="fa fa-trash"></i>
                                    </asp:LinkButton>
                                </div>
                            </ItemTemplate>
                            <HeaderStyle Width="90" />
                        </asp:TemplateField>

                    </Columns>
                </asp:GridView>
                </div>
            </div>

            <asp:Label ID="lblListMsg" runat="server" CssClass="text-danger mt-2 d-block" />
        </ContentTemplate>
    </asp:UpdatePanel>

    <!-- ================= MODAL (ADD/EDIT) ================= -->
    <div class="modal fade" id="ruleModal" tabindex="-1" role="dialog" aria-hidden="true">
        <div class="modal-dialog modal-lg" role="document">
            <div class="modal-content">

                <div class="modal-header">
                    <h5 class="modal-title"><asp:Literal ID="litModalTitle" runat="server" /></h5>
                    <button type="button" class="btn btn-danger btn-close-yield" data-dismiss="modal"><i class="fa fa-xmark"></i> Close</button>
                   
                </div>

                <div class="modal-body">
                    <asp:UpdatePanel ID="upModal" runat="server" UpdateMode="Conditional">
                        <ContentTemplate>

                            <asp:HiddenField ID="hdRuleId" runat="server" />
                            <asp:HiddenField ID="hdDaysCsv" runat="server" />
                             <asp:Label ID="lblModalMsg" runat="server" style="color:red;" CssClass="text-danger" Text="" />
                            <div class="form-row">
                                <div class="form-group col-md-8">
                                    <label>Name</label>
                                    <asp:TextBox ID="txtName" runat="server" CssClass="form-control" placeholder="Name..." />
                                </div>
                                <div class="form-group col-md-4">
                                    <label>Priority</label>
                                    <asp:TextBox ID="txtPriority" runat="server" CssClass="form-control" placeholder="0" />
                                </div>
                            </div>

                            <div class="section-card">
                                <div class="section-title"><i class="fa-solid fa-layer-group"></i> Rate Plan & Category Selection</div>

                                <div class="selector-row">
                                    <div class="selector-wrap">
                                        <div class="field-label"><i class="fa-solid fa-tags"></i> Rate Plans</div>
                                        <asp:ListBox ID="lstRatePlans" runat="server" CssClass="form-control modern-multi" SelectionMode="Multiple"></asp:ListBox>
                                    </div>
                                    <div class="selector-tools">
                                        <button type="button" class="btn-ico btn-plus" id="btnRatePlansPlus" title="Select All Rate Plans">
                                            <i class="fa-solid fa-plus"></i>
                                        </button>
                                        <button type="button" class="btn-ico btn-x" id="btnRatePlansClear" title="Clear Rate Plans">
                                            <i class="fa-solid fa-xmark"></i>
                                        </button>
                                    </div>
                                </div>

                                <div class="selector-row" style="margin-bottom:0;">
                                    <div class="selector-wrap">
                                        <div class="field-label"><i class="fa-solid fa-bed"></i> Room Types / Categories</div>
                                        <asp:ListBox ID="lstRoomTypes" runat="server" CssClass="form-control modern-multi" SelectionMode="Multiple"></asp:ListBox>
                                    </div>
                                    <div class="selector-tools">
                                        <button type="button" class="btn-ico btn-plus" id="btnRoomTypesPlus" title="Select All Room Types">
                                            <i class="fa-solid fa-plus"></i>
                                        </button>
                                        <button type="button" class="btn-ico btn-x" id="btnRoomTypesClear" title="Clear Room Types">
                                            <i class="fa-solid fa-xmark"></i>
                                        </button>
                                    </div>
                                </div>
                            </div>

                            <div class="form-row">
                                <div class="form-group col-md-6">
                                    <label>Staying From</label>
                                    <asp:TextBox ID="txtStayFrom" TextMode="Date" runat="server" CssClass="form-control" />
                                </div>
                                <div class="form-group col-md-6">
                                    <label>Staying To</label>
                                    <asp:TextBox ID="txtStayTo" TextMode="Date" runat="server" CssClass="form-control" />
                                </div>
                            </div>

                            <div class="form-group">
                                <label>Applicable To</label>
                                <div class="pill-row">
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="7" /> Sunday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="1" /> Monday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="2" /> Tuesday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="3" /> Wednesday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="4" /> Thursday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="5" /> Friday</label>
                                    <label class="btn btn-light btn-sm"><input type="checkbox" class="dow" value="6" /> Saturday</label>
                                </div>
                            </div>

                            <div class="form-group">
                                <label>Rule Type</label>
                                <asp:DropDownList ID="ddlRuleType" runat="server" CssClass="form-control">
                                    <%--<asp:ListItem Value="ADVANCE_BOOKING" Visible="false" Text="Advance Booking" />
                                    <asp:ListItem Value="CLOSE_AT_OCCUPANCY" Visible="false"  Text="Close At Occupancy Level" />--%>
                                    <asp:ListItem Value="OCC_PCT_PROPERTY" Text="Occupancy Percentage - Property" />
                                    <asp:ListItem Value="OCC_PCT_ROOMTYPE" Text="Occupancy Percentage - Room Type" />
                                   <%-- <asp:ListItem Value="TIMED_DISCOUNT" Visible="false"  Text="Timed Discount" />--%>
                                </asp:DropDownList>
                            </div>

                            <div class="form-row">
                                <div class="form-group col-md-4">
                                    <label>Change Type</label>
                                    <asp:DropDownList ID="ddlChangeType" runat="server" CssClass="form-control">
                                        <asp:ListItem Value="" Text="Change Type..." />
                                        <asp:ListItem Value="INCREASE" Text="Increase" />
                                        <asp:ListItem Value="DECREASE" Text="Decrease" />
                                        <asp:ListItem Value="SET" Text="Set To" />
                                    </asp:DropDownList>
                                </div>
                                <div class="form-group col-md-4">
                                    <label>Change Value</label>
                                    <asp:TextBox ID="txtChangeValue" runat="server" CssClass="form-control" placeholder="Change Value..." />
                                </div>
                                <div class="form-group col-md-4">
                                    <label>Change Unit</label>
                                    <asp:DropDownList ID="ddlChangeUnit" runat="server" CssClass="form-control">
                                        <asp:ListItem Value="" Text="Change Unit..." />
                                        <asp:ListItem Value="PERCENT" Text="Percentage" />
                                        <asp:ListItem Value="AMOUNT" Text="Value" />
                                    </asp:DropDownList>
                                </div>
                            </div>

                            <div class="form-group">
                                <label class="text-danger">Threshold</label>

                                <div id="thAdvance" class="threshold-box">
                                    When booking between
                                    <asp:TextBox ID="txtThMin" runat="server" CssClass="form-control d-inline-block" Style="width:120px" />
                                    and
                                    <asp:TextBox ID="txtThMax" runat="server" CssClass="form-control d-inline-block" Style="width:120px" />
                                    days in advance
                                </div>

                                <div id="thOcc" class="threshold-box" style="display:none;">
                                    When occupancy between
                                    <asp:TextBox ID="txtOccMin" runat="server" CssClass="form-control d-inline-block" Style="width:120px" />
                                    and
                                    <asp:TextBox ID="txtOccMax" runat="server" CssClass="form-control d-inline-block" Style="width:120px" />
                                    %
                                </div>

                                <div id="thTimed" class="threshold-box" style="display:none;">
                                    Apply between time
                                    <asp:TextBox ID="txtTimeFrom" runat="server" CssClass="form-control d-inline-block" Style="width:140px" placeholder="HH:mm" />
                                    and
                                    <asp:TextBox ID="txtTimeTo" runat="server" CssClass="form-control d-inline-block" Style="width:140px" placeholder="HH:mm" />
                                </div>
                            </div>

                            <div class="form-group d-none">
                                <label>Exclude Date Ranges</label>

                                <asp:Repeater ID="rptExclude" runat="server" OnItemCommand="rptExclude_ItemCommand">
                                    <ItemTemplate>
                                        <div class="form-row align-items-center mb-2">
                                            <div class="col-md-5">
                                                <asp:TextBox ID="txtExFrom" TextMode="Date" runat="server" CssClass="form-control"
                                                    Text='<%# Eval("From") %>' />
                                            </div>
                                            <div class="col-md-5">
                                                <asp:TextBox ID="txtExTo" TextMode="Date" runat="server" CssClass="form-control"
                                                    Text='<%# Eval("To") %>' />
                                            </div>
                                            <div class="col-md-2">
                                                <asp:LinkButton ID="btnRemove" runat="server" CommandName="remove"
                                                    CommandArgument='<%# Container.ItemIndex %>' CssClass="btn btn-outline-danger btn-sm">
                                                    Remove
                                                </asp:LinkButton>
                                            </div>
                                        </div>
                                    </ItemTemplate>
                                </asp:Repeater>

                                <asp:LinkButton ID="btnAddExclude" runat="server" Visible="false" CssClass="btn btn-link p-0" OnClick="btnAddExclude_Click">
                                    + Add Excluded Range
                                </asp:LinkButton>
                            </div>

                            <div class="form-group">
                                <asp:CheckBox ID="chkActive" runat="server" Text=" Active" Checked="true" />
                            </div>

                            

                        </ContentTemplate>
                    </asp:UpdatePanel>
                </div>

                <div class="modal-footer">
                    <asp:LinkButton ID="btnSave" runat="server" CssClass="btn btn-success btn-save-yield" OnClick="btnSave_Click">
                        Save
                    </asp:LinkButton>
                    <button type="button" class="btn btn-secondary btn-cancel-yield" data-dismiss="modal">Cancel</button>
                </div>

            </div>
        </div>
    </div>

    </div>


    <!-- ================= BEAUTIFUL STATUS CONFIRMATION MODAL ================= -->
    <div class="modal fade" id="yieldStatusConfirmModal" tabindex="-1" role="dialog" aria-hidden="true" data-backdrop="static" data-keyboard="false">
        <div class="modal-dialog modal-dialog-centered" role="document">
            <div class="modal-content">
                <div class="yield-confirm-head">
                    <div class="yield-confirm-icon">
                        <i class="fa-solid fa-triangle-exclamation"></i>
                    </div>
                    <h5 class="yield-confirm-title" id="yieldStatusConfirmTitle">Confirm Status Change</h5>
                    <p class="yield-confirm-subtitle" id="yieldStatusConfirmSubtitle">
                        Please review before continuing.
                    </p>
                </div>
                <div class="yield-confirm-body">
                    <div class="yield-warning-box" id="yieldStatusWarningBox">
                        <i class="fa-solid fa-circle-info"></i>
                        <span id="yieldStatusWarningText"></span>
                    </div>
                    <ul class="yield-info-list" id="yieldStatusInfoList"></ul>
                </div>
                <div class="yield-confirm-footer">
                    <button type="button" class="btn-yield-keep" data-dismiss="modal">
                        <i class="fa-solid fa-xmark"></i> Cancel
                    </button>
                    <button type="button" class="btn-yield-confirm" id="btnConfirmYieldStatusChange">
                        <i class="fa-solid fa-check"></i> Yes, Continue
                    </button>
                </div>
            </div>
        </div>
    </div>

    <script src="https://cdn.jsdelivr.net/npm/jquery@3.7.1/dist/jquery.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@4.6.2/dist/js/bootstrap.bundle.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/select2@4.1.0-rc.0/dist/js/select2.min.js"></script>

    <script>
        function initSelect2() {
            var $rp = $('#<%= lstRatePlans.ClientID %>');
            var $rt = $('#<%= lstRoomTypes.ClientID %>');

            if ($rp.hasClass("select2-hidden-accessible")) $rp.select2('destroy');
            if ($rt.hasClass("select2-hidden-accessible")) $rt.select2('destroy');

            $rp.select2({
                dropdownParent: $('#ruleModal'),
                placeholder: "Select Rate Plan(s)...",
                width: "100%",
                closeOnSelect: false,
                allowClear: true
            });

            $rt.select2({
                dropdownParent: $('#ruleModal'),
                placeholder: "Select Room Type(s) / Category...",
                width: "100%",
                closeOnSelect: false,
                allowClear: true
            });
        }

        function selectAllOptions(selector) {
            var values = $(selector).find('option').map(function () {
                return this.value;
            }).get();

            $(selector).val(values).trigger('change');
        }

        function clearAllOptions(selector) {
            $(selector).val(null).trigger('change');
        }

        function wireMultiSelectButtons() {
            $('#btnRatePlansPlus').off('click').on('click', function () {
                selectAllOptions('#<%= lstRatePlans.ClientID %>');
            });

            $('#btnRatePlansClear').off('click').on('click', function () {
                clearAllOptions('#<%= lstRatePlans.ClientID %>');
            });

            $('#btnRoomTypesPlus').off('click').on('click', function () {
                selectAllOptions('#<%= lstRoomTypes.ClientID %>');
            });

            $('#btnRoomTypesClear').off('click').on('click', function () {
                clearAllOptions('#<%= lstRoomTypes.ClientID %>');
            });
        }


        var pendingYieldStatusButton = null;

        function showYieldStatusConfirm(btn, isCurrentlyActive) {
            if ($(btn).data('yield-confirmed') === true) {
                $(btn).data('yield-confirmed', false);
                return true;
            }

            pendingYieldStatusButton = btn;

            if (isCurrentlyActive) {
                $('#yieldStatusConfirmTitle').text('Make Yield Rule Inactive?');
                $('#yieldStatusConfirmSubtitle').text('This will remove the rule effect from future applicable dates.');
                $('#yieldStatusWarningText').text('If this rule is currently applied on any future date, those rates will be changed back to the default rates from category_plan and the restored rates will be uploaded to Channex.');
                $('#yieldStatusInfoList').html(
                    '<li><i class="fa-solid fa-calendar-day"></i><span>Only dates from today onward until the rule end date will be updated.</span></li>' +
                    '<li><i class="fa-solid fa-tags"></i><span>Only the selected rate plans and room types of this rule will be affected.</span></li>' +
                    '<li><i class="fa-solid fa-cloud-arrow-up"></i><span>After saving Please check your inventory and update rates if you want.</span></li>'
                );
                $('#btnConfirmYieldStatusChange').html('<i class="fa-solid fa-rotate-left"></i> Inactive & Restore Rates');
            }
            else {
                $('#yieldStatusConfirmTitle').text('Make Yield Rule Active?');
                $('#yieldStatusConfirmSubtitle').text('This rule will become active again for future yield processing.');
                $('#yieldStatusWarningText').text('The rule status will be changed to Active. Rates will not be restored to default in this action.');
                $('#yieldStatusInfoList').html(
                    '<li><i class="fa-solid fa-circle-check"></i><span>The rule will be available for future yield rule application.</span></li>' +
                    '<li><i class="fa-solid fa-clock"></i><span>No immediate default-rate restore will be performed.</span></li>'
                );
                $('#btnConfirmYieldStatusChange').html('<i class="fa-solid fa-check"></i> Make Active');
            }

            $('#yieldStatusConfirmModal').modal('show');
            return false;
        }

        function confirmPendingYieldStatusChange() {
            if (!pendingYieldStatusButton) return;

            var btn = pendingYieldStatusButton;
            pendingYieldStatusButton = null;

            $('#yieldStatusConfirmModal').modal('hide');
            $(btn).data('yield-confirmed', true);
            btn.click();
        }

        function syncDays() {
            var arr = [];
            $('.dow:checked').each(function () { arr.push($(this).val()); });
            $('#<%= hdDaysCsv.ClientID %>').val(arr.join(','));
        }

        function setDaysFromCsv(csv) {
            $('.dow').prop('checked', false);
            if (!csv) return;

            var set = csv.split(',');
            $('.dow').each(function () {
                if (set.indexOf($(this).val()) >= 0) {
                    $(this).prop('checked', true);
                }
            });

            syncDays();
        }

        function toggleThresholdByRuleType() {
            var rt = $('#<%= ddlRuleType.ClientID %>').val();
            $('#thAdvance,#thOcc,#thTimed').hide();

            if (rt === 'ADVANCE_BOOKING') {
                $('#thAdvance').show();
            }
            else if (rt === 'CLOSE_AT_OCCUPANCY' || rt === 'OCC_PCT_PROPERTY' || rt === 'OCC_PCT_ROOMTYPE') {
                $('#thOcc').show();
            }
            else if (rt === 'TIMED_DISCOUNT') {
                $('#thTimed').show();
            }
        }

        function applyDayPillStyles() {
            $('.dow').each(function () {
                var $lbl = $(this).closest('label.btn');

                if ($(this).is(':checked')) {
                    $lbl.css({
                        background: 'rgba(16,183,176,.14)',
                        color: '#083b39',
                        borderColor: 'rgba(16,183,176,.45)'
                    });
                }
                else {
                    $lbl.css({
                        background: '#fff',
                        color: '#64748b',
                        borderColor: 'rgba(15,23,42,.10)'
                    });
                }
            });
        }

        function wireModalEvents() {
            $('.dow').off('change').on('change', function () {
                syncDays();
                applyDayPillStyles();
            });

            $('#<%= ddlRuleType.ClientID %>').off('change').on('change', function () {
                toggleThresholdByRuleType();
            });

            wireMultiSelectButtons();
        }

        function openRuleModal(daysCsv) {
            $('#ruleModal').modal('show');

            $('#ruleModal').off('shown.bs.modal').on('shown.bs.modal', function () {
                initSelect2();
                setDaysFromCsv(daysCsv || "1,2,3,4,5,6,7");
                toggleThresholdByRuleType();
                wireModalEvents();
                $('#btnConfirmYieldStatusChange').off('click').on('click', confirmPendingYieldStatusChange);
                applyDayPillStyles();
                syncDays();
            });
        }

        $(document).ready(function () {
            wireModalEvents();
            $('#btnConfirmYieldStatusChange').off('click').on('click', confirmPendingYieldStatusChange);
        });

        if (window.Sys && Sys.WebForms && Sys.WebForms.PageRequestManager) {
            Sys.WebForms.PageRequestManager.getInstance().add_endRequest(function () {
                wireModalEvents();
                $('#btnConfirmYieldStatusChange').off('click').on('click', confirmPendingYieldStatusChange);
                applyDayPillStyles();

                if ($('#ruleModal').hasClass('show')) {
                    initSelect2();
                    toggleThresholdByRuleType();
                }
            });
        }
    </script>


</asp:Content>
