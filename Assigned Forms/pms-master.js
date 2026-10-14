/* ---------------------------------------------------------------------------
   ORA PMS master shell.

   Every window.pms* function the previous version exposed is still here with
   the same name and signature. Views across the product call these from inline
   onclick attributes, so renaming one would break pages nobody thought to
   check.
   --------------------------------------------------------------------------- */

(function () {
    'use strict';

    const MOBILE_BREAKPOINT = 900;

    function normalize(value) {
        return (value || '').toString().trim().toLowerCase();
    }

    function isMobile() {
        return window.innerWidth <= MOBILE_BREAKPOINT;
    }

    function store(key, value) {
        // A private window throws on localStorage rather than returning null,
        // and a navigation shell that dies there is worse than one that forgets.
        try { localStorage.setItem(key, value); } catch (e) { /* ignore */ }
    }

    function read(key) {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    }

    // -----------------------------------------------------------------------
    // Active page
    // -----------------------------------------------------------------------

    function currentPath() {
        return (window.location.pathname || '').replace(/\/+$/, '').toLowerCase();
    }

    function linkMatches(link) {
        const href = link.getAttribute('href') || '';
        if (!href || href === '#') return false;

        let path;
        try {
            path = new URL(href, window.location.origin).pathname.replace(/\/+$/, '').toLowerCase();
        } catch (e) {
            return false;
        }

        if (!path || path === '/') return false;
        return currentPath() === path;
    }

    function activateCurrentSidebarPage() {
        const links = document.querySelectorAll('.pms-sidebar-menu-link');
        let matched = null;

        links.forEach(function (link) {
            if (!matched && linkMatches(link)) matched = link;
        });

        // Falls back to the page name remembered when the link was clicked, so
        // a URL carrying a query string still highlights the right row.
        if (!matched) {
            const remembered = normalize(read('sidebar_current_page'));

            if (remembered) {
                links.forEach(function (link) {
                    if (!matched && normalize(link.getAttribute('data-page')) === remembered) matched = link;
                });
            }
        }

        if (!matched) return;

        matched.classList.add('active-menu');

        const submenu = matched.closest('.pms-submenu');
        if (!submenu) return;

        const category = submenu.previousElementSibling;
        if (category && category.classList.contains('pms-category-link')) category.classList.add('open');

        // Bring the open group into view when the list is long enough to scroll.
        window.setTimeout(function () {
            const scroll = document.querySelector('.pms-sidebar-scroll');
            if (!scroll) return;

            const top = matched.offsetTop;
            if (top > scroll.clientHeight - 60) scroll.scrollTop = top - scroll.clientHeight / 2;
        }, 60);
    }

    function activateCurrentTopbarPage() {
        document.querySelectorAll('.pms-quick-link').forEach(function (link) {
            if (linkMatches(link)) link.classList.add('active-menu');
        });
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    window.pmsToggleMenu = function (element) {
        if (!element) return;

        // Collapsed there is nowhere for a submenu to appear, so open the
        // sidebar instead of toggling a panel the user cannot see.
        if (!isMobile() && document.body.classList.contains('sidebar-collapsed')) {
            window.pmsToggleNav();

            window.setTimeout(function () {
                element.classList.add('open');
            }, 60);

            return;
        }

        const wasOpen = element.classList.contains('open');

        document.querySelectorAll('.pms-category-link.open').forEach(function (other) {
            if (other !== element) other.classList.remove('open');
        });

        element.classList.toggle('open', !wasOpen);
    };

    window.pmsCloseMobileNav = function () {
        document.body.classList.remove('mobile-sidebar-open');
        syncNavButtons();
    };

    window.pmsSetSelectedSidebar = function (element) {
        if (!element) return;

        store('sidebar_current_page', normalize(element.getAttribute('data-page') || ''));
        if (isMobile()) window.pmsCloseMobileNav();
    };

    window.pmsToggleNav = function () {
        if (isMobile()) {
            document.body.classList.toggle('mobile-sidebar-open');
        } else {
            document.body.classList.toggle('sidebar-collapsed');
            store('sidebar_collapsed', document.body.classList.contains('sidebar-collapsed') ? '1' : '0');
        }

        syncNavButtons();
    };

    function syncNavButtons() {
        const desktop = document.getElementById('pmsDesktopNavToggle');
        const mobile = document.getElementById('pmsMobileNavToggle');

        if (desktop) {
            const expanded = !document.body.classList.contains('sidebar-collapsed');
            desktop.setAttribute('aria-expanded', expanded ? 'true' : 'false');
            desktop.setAttribute('aria-label', expanded ? 'Collapse navigation' : 'Expand navigation');
        }

        if (mobile) {
            const open = document.body.classList.contains('mobile-sidebar-open');
            mobile.setAttribute('aria-expanded', open ? 'true' : 'false');
            mobile.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
        }
    }

    window.pmsToggleFullHeader = function () {
        document.body.classList.toggle('header-hidden');
        store('header_hidden', document.body.classList.contains('header-hidden') ? '1' : '0');
    };

    // Kept so any page still calling it does not throw. The profile popup was
    // replaced by the four buttons in the sidebar footer.
    window.pmsToggleProfile = function () {
        window.pmsOpenChangePassword();
    };

    // The top bar quick links no longer scroll, but a view somewhere may still
    // call this from an onclick that was never removed.
    window.pmsScrollTopbar = function () { /* no longer needed */ };

    // -----------------------------------------------------------------------
    // Modals
    // -----------------------------------------------------------------------

    function openModal(id) {
        const overlay = document.getElementById('pmsMasterOverlay');
        const modal = document.getElementById(id);
        if (!modal) return;

        closeAllModals();

        if (overlay) overlay.classList.add('open');
        modal.classList.add('open');

        const field = modal.querySelector('input:not([type=hidden])');
        if (field) window.setTimeout(function () { field.focus(); }, 40);
    }

    function closeAllModals() {
        document.querySelectorAll('.pms-master-modal.open').forEach(function (modal) {
            modal.classList.remove('open');
        });
    }

    window.pmsOpenChangePassword = function () {
        openModal('pmsChangePasswordModal');
    };

    window.pmsOpenAbout = function () {
        openModal('pmsAboutModal');
    };

    window.pmsCloseMasterModal = function () {
        const overlay = document.getElementById('pmsMasterOverlay');
        if (overlay) overlay.classList.remove('open');
        closeAllModals();
    };

    // -----------------------------------------------------------------------
    // Support chat
    //
    // No provider is wired up yet. When one is, replace the body of this
    // function with that widget's open call - everything else stays as it is.
    // A third-party widget also needs its domain added to the CSP, or it will
    // be blocked silently once the policy is enforced.
    // -----------------------------------------------------------------------

    window.pmsOpenSupportChat = function () {
        if (window.Tawk_API && typeof window.Tawk_API.maximize === 'function') {
            window.Tawk_API.maximize();
            return;
        }

        if (window.$crisp && typeof window.$crisp.push === 'function') {
            window.$crisp.push(['do', 'chat:open']);
            return;
        }

        window.pmsSupportChatUnavailable();
    };

    window.pmsSupportChatUnavailable = function () {
        const existing = document.getElementById('pmsChatToast');
        if (existing) existing.remove();

        const toast = document.createElement('div');
        toast.id = 'pmsChatToast';
        toast.className = 'pms-master-toast warning';
        toast.textContent = 'Support chat is not connected yet.';

        document.body.appendChild(toast);

        window.setTimeout(function () {
            toast.style.opacity = '0';
            window.setTimeout(function () { toast.remove(); }, 250);
        }, 2600);
    };

    window.pmsSetSupportChatBadge = function (count) {
        const button = document.getElementById('pmsSupportChat');
        if (!button) return;

        const existing = button.querySelector('.pms-chat-badge');
        if (existing) existing.remove();

        const value = parseInt(count, 10);
        if (!value || value < 1) return;

        const badge = document.createElement('span');
        badge.className = 'pms-chat-badge';
        badge.textContent = value > 9 ? '9+' : String(value);

        button.appendChild(badge);
    };

    // -----------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------

    function restoreShellState() {
        // The collapsed rail is a desktop state only; on a phone the sidebar is
        // a drawer, and a 66px rail there would be unusable.
        if (!isMobile() && read('sidebar_collapsed') === '1') {
            document.body.classList.add('sidebar-collapsed');
        }

        if (read('header_hidden') === '1') {
            document.body.classList.add('header-hidden');
        }

        syncNavButtons();
    }

    document.addEventListener('DOMContentLoaded', function () {
        restoreShellState();
        activateCurrentSidebarPage();
        activateCurrentTopbarPage();

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
        if (!isMobile()) {
            document.body.classList.remove('mobile-sidebar-open');
            if (read('sidebar_collapsed') === '1') document.body.classList.add('sidebar-collapsed');
        } else {
            document.body.classList.remove('sidebar-collapsed');
        }

        syncNavButtons();
    });

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;

        if (document.querySelector('.pms-master-modal.open')) {
            window.pmsCloseMasterModal();
            return;
        }

        if (document.body.classList.contains('mobile-sidebar-open')) {
            window.pmsCloseMobileNav();
        }
    });
})();
