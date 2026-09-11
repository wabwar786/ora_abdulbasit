(function () {
    'use strict';

    const categoryIcons = {
        'Front Desk': 'fa-solid fa-bell-concierge',
        'Reservation': 'fa-solid fa-calendar-check',
        'Accounts': 'fa-solid fa-file-invoice-dollar',
        'Reports': 'fa-solid fa-chart-line',
        'House Keeping': 'fa-solid fa-broom',
        'Maintenance': 'fa-solid fa-screwdriver-wrench',
        'Restaurant': 'fa-solid fa-utensils',
        'Inventory': 'fa-solid fa-boxes-stacked',
        'Settings': 'fa-solid fa-gear',
        'Users': 'fa-solid fa-user-gear',
        'Council': 'fa-solid fa-building-columns',
        'Dashboard': 'fa-solid fa-gauge-high',
        'Support': 'fa-solid fa-headset'
    };

    const menuIcons = {
        'Calendar': 'fa-solid fa-calendar-days',
        'Generate Reservation': 'fa-solid fa-file-circle-plus',
        'Payment Link': 'fa-solid fa-link',
        'Make Payment': 'fa-solid fa-credit-card',
        'Check In': 'fa-solid fa-right-to-bracket',
        'Check Out': 'fa-solid fa-right-from-bracket',
        'Invoice': 'fa-solid fa-receipt',
        'Customer List': 'fa-solid fa-users',
        'Reservation List': 'fa-solid fa-list-check',
        'Room Status': 'fa-solid fa-bed',
        'Block Room': 'fa-solid fa-ban',
        'Bulk Check In': 'fa-solid fa-layer-group',
        'Bulk Check Out': 'fa-solid fa-layer-group',
        'Occupancy Report': 'fa-solid fa-chart-pie',
        'Income Comparison': 'fa-solid fa-scale-balanced',
        'Users': 'fa-solid fa-user',
        'Roles': 'fa-solid fa-user-shield',
        'Audit Log': 'fa-solid fa-clipboard-list',
        'Notifications': 'fa-solid fa-bell'
    };

    function normalize(value) {
        return (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
    }

    function setIcon(element, classNames) {
        if (!element) return;
        element.className = element.className.replace(/\bfa-\S+/g, '').trim();
        classNames.split(' ').forEach(function (className) {
            if (className) element.classList.add(className);
        });
    }

    function applySidebarIcons() {
        const sidebar = document.getElementById('sidebar');
        if (!sidebar) return;

        sidebar.querySelectorAll('.pms-category-link').forEach(function (link) {
            const text = (link.querySelector('.pms-cat-text') || {}).textContent || '';
            const key = Object.keys(categoryIcons).find(function (name) {
                return normalize(name) === normalize(text);
            });
            setIcon(link.querySelector('.pms-cat-ico'), key ? categoryIcons[key] : 'fa-solid fa-layer-group');
        });

        sidebar.querySelectorAll('.pms-sidebar-menu-link').forEach(function (link) {
            const text = (link.querySelector('.pms-menu-text') || {}).textContent || '';
            const key = Object.keys(menuIcons).find(function (name) {
                return normalize(name) === normalize(text);
            });
            setIcon(link.querySelector('.pms-menu-ico'), key ? menuIcons[key] : 'fa-regular fa-circle-dot');
        });
    }

    function canonicalPageName(value) {
        const compact = normalize((value || '').replace(/\.aspx$/i, '')).replace(/[^a-z0-9]/g, '');
        if (['availability', 'availabilitysetup', 'availibilty', 'availibiltysetup'].includes(compact)) return 'availability';
        if (['calendar', 'calender', 'frontdeskcalendar', 'frontdeskcalender'].includes(compact)) return 'calendar';
        if (['createreservation', 'newreservation', 'extendedreservation'].includes(compact)) return 'createreservation';
        return compact;
    }

    function currentPageName() {
        const path = (window.location.pathname || '').replace(/\/+$/, '');
        let name = path.substring(path.lastIndexOf('/') + 1) || 'Dashboard';
        return canonicalPageName(name);
    }


    function activateCurrentTopbarPage() {
        const current = currentPageName();
        document.querySelectorAll('#pmsTopbarList .pms-topbar-link').forEach(function (link) {
            const page = canonicalPageName(link.getAttribute('data-page') || '');
            link.classList.toggle('active-menu', page === current);
        });
    }

    function closeAllSidebarMenus() {
        document.querySelectorAll('#sidebar .pms-submenu').forEach(function (submenu) {
            submenu.style.display = 'none';
        });
        document.querySelectorAll('#sidebar .pms-category-link').forEach(function (category) {
            category.classList.remove('open');
        });
        document.querySelectorAll('#sidebar .pms-sidebar-menu-link').forEach(function (menu) {
            menu.classList.remove('active-menu');
        });
    }

    function activateCurrentSidebarPage() {
        const current = currentPageName();
        closeAllSidebarMenus();

        let matched = null;
        document.querySelectorAll('#sidebar .pms-sidebar-menu-link').forEach(function (menu) {
            const page = canonicalPageName(menu.getAttribute('data-page') || '');
            if (page === current) matched = menu;
        });

        if (!matched) {
            const first = document.querySelector('#sidebar .pms-category-link');
            if (first) {
                first.classList.add('open');
                const submenu = first.nextElementSibling;
                if (submenu && submenu.classList.contains('pms-submenu')) submenu.style.display = 'block';
            }
            return;
        }

        matched.classList.add('active-menu');
        const submenu = matched.closest('.pms-submenu');
        if (submenu) {
            submenu.style.display = 'block';
            const category = submenu.previousElementSibling;
            if (category && category.classList.contains('pms-category-link')) category.classList.add('open');
        }
    }

    window.pmsToggleMenu = function (element) {
        const submenu = element ? element.nextElementSibling : null;
        if (!submenu || !submenu.classList.contains('pms-submenu')) return false;
        const isOpen = submenu.style.display === 'block';
        closeAllSidebarMenus();
        if (!isOpen) {
            submenu.style.display = 'block';
            element.classList.add('open');
        } else {
            activateCurrentSidebarPage();
        }
        return false;
    };

    function syncNavButtons() {
        const mobileOpen = document.body.classList.contains('mobile-sidebar-open');
        const desktopCollapsed = document.body.classList.contains('sidebar-collapsed');
        const desktopToggle = document.getElementById('pmsDesktopNavToggle');
        const mobileToggle = document.getElementById('pmsMobileNavToggle');

        if (desktopToggle) {
            desktopToggle.setAttribute('aria-expanded', desktopCollapsed ? 'false' : 'true');
            desktopToggle.setAttribute('aria-label', desktopCollapsed ? 'Expand navigation' : 'Collapse navigation');
        }
        if (mobileToggle) {
            mobileToggle.setAttribute('aria-expanded', mobileOpen ? 'true' : 'false');
            mobileToggle.setAttribute('aria-label', mobileOpen ? 'Close navigation' : 'Open navigation');
        }
    }

    window.pmsCloseMobileNav = function () {
        document.body.classList.remove('mobile-sidebar-open');
        syncNavButtons();
    };

    window.pmsSetSelectedSidebar = function (element) {
        if (!element) return;
        localStorage.setItem('sidebar_current_page', normalize(element.getAttribute('data-page') || ''));
        if (window.innerWidth <= 900) window.pmsCloseMobileNav();
    };

    window.pmsToggleNav = function () {
        if (window.innerWidth <= 900) {
            // Desktop collapsed state must never block the mobile drawer.
            document.body.classList.remove('sidebar-collapsed');
            document.body.classList.toggle('mobile-sidebar-open');
        } else {
            document.body.classList.remove('mobile-sidebar-open');
            document.body.classList.toggle('sidebar-collapsed');
            localStorage.setItem('sidebar_collapsed', document.body.classList.contains('sidebar-collapsed') ? '1' : '0');
        }
        syncNavButtons();
    };

    function updateTopbarScrollControls() {
        const row = document.getElementById('pmsTopbarList');
        const previousButton = document.getElementById('pmsTopbarPrev');
        const nextButton = document.getElementById('pmsTopbarNext');
        if (!row || !previousButton || !nextButton) return;

        const tolerance = 3;
        const hasOverflow = row.scrollWidth > row.clientWidth + tolerance;
        const atStart = row.scrollLeft <= tolerance;
        const atEnd = row.scrollLeft + row.clientWidth >= row.scrollWidth - tolerance;

        // The controls do not occupy header space unless scrolling is actually possible.
        previousButton.hidden = !hasOverflow || atStart;
        nextButton.hidden = !hasOverflow || atEnd;
        previousButton.setAttribute('aria-hidden', previousButton.hidden ? 'true' : 'false');
        nextButton.setAttribute('aria-hidden', nextButton.hidden ? 'true' : 'false');
    }

    function wireTopbarScrollControls() {
        const row = document.getElementById('pmsTopbarList');
        if (!row) return;

        row.addEventListener('scroll', updateTopbarScrollControls, { passive: true });
        window.addEventListener('resize', updateTopbarScrollControls, { passive: true });

        if (window.ResizeObserver) {
            const observer = new ResizeObserver(updateTopbarScrollControls);
            observer.observe(row);
            if (row.parentElement) observer.observe(row.parentElement);
        }

        window.requestAnimationFrame(updateTopbarScrollControls);
        window.setTimeout(updateTopbarScrollControls, 120);
    }

    window.pmsScrollTopbar = function (direction) {
        const row = document.getElementById('pmsTopbarList');
        if (!row) return;
        row.scrollBy({ left: direction === 'left' ? -220 : 220, behavior: 'smooth' });
        window.setTimeout(updateTopbarScrollControls, 260);
    };

    window.pmsToggleFullHeader = function () {
        document.body.classList.toggle('header-hidden');
        localStorage.setItem('header_hidden', document.body.classList.contains('header-hidden') ? '1' : '0');
    };

    window.pmsToggleProfile = function () {
        const popup = document.getElementById('pmsProfilePopup');
        if (!popup) return;
        popup.classList.toggle('open');
    };

    window.pmsOpenChangePassword = function () {
        const overlay = document.getElementById('pmsMasterOverlay');
        const popup = document.getElementById('pmsChangePasswordModal');
        const profile = document.getElementById('pmsProfilePopup');
        if (profile) profile.classList.remove('open');
        if (overlay) overlay.classList.add('open');
        if (popup) popup.classList.add('open');
    };

    window.pmsCloseMasterModal = function () {
        const overlay = document.getElementById('pmsMasterOverlay');
        if (overlay) overlay.classList.remove('open');
        document.querySelectorAll('.pms-master-modal.open').forEach(function (modal) {
            modal.classList.remove('open');
        });
    };

    function wireOutsideProfileClose() {
        document.addEventListener('click', function (event) {
            const popup = document.getElementById('pmsProfilePopup');
            const button = document.getElementById('pmsProfileButton');
            if (!popup || !button || !popup.classList.contains('open')) return;
            if (popup.contains(event.target) || button.contains(event.target)) return;
            popup.classList.remove('open');
        }, true);
    }

    function restoreShellState() {
        document.body.classList.remove('mobile-sidebar-open');
        if (window.innerWidth > 900 && localStorage.getItem('sidebar_collapsed') === '1') {
            document.body.classList.add('sidebar-collapsed');
        } else {
            document.body.classList.remove('sidebar-collapsed');
        }
        if (localStorage.getItem('header_hidden') === '1') {
            document.body.classList.add('header-hidden');
        }
        syncNavButtons();
    }

    document.addEventListener('DOMContentLoaded', function () {
        restoreShellState();
        applySidebarIcons();
        activateCurrentSidebarPage();
        activateCurrentTopbarPage();
        wireTopbarScrollControls();
        wireOutsideProfileClose();

        if (document.body.getAttribute('data-open-change-password') === '1') {
            window.pmsOpenChangePassword();
        }

        const toast = document.getElementById('pmsMasterToast');
        if (toast) {
            window.setTimeout(function () {
                toast.style.opacity = '0';
                window.setTimeout(function () { toast.remove(); }, 250);
            }, 4200);
        }
    });

    window.addEventListener('resize', function () {
        if (window.innerWidth > 900) {
            document.body.classList.remove('mobile-sidebar-open');
            if (localStorage.getItem('sidebar_collapsed') === '1') document.body.classList.add('sidebar-collapsed');
        } else {
            // Mobile always uses the slide-out drawer, never the desktop 68px state.
            document.body.classList.remove('sidebar-collapsed');
        }
        syncNavButtons();
        updateTopbarScrollControls();
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && document.body.classList.contains('mobile-sidebar-open')) {
            window.pmsCloseMobileNav();
        }
    });
})();
