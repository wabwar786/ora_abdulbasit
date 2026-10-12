(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const root = $('restaurant-inventory');
  if (!root) return;
  const status = $('r-inventory-status');
  let statusTimer;
  let busy = false;
  const notify = (message, error = false, persistent = false) => {
    if (!status) return;
    clearTimeout(statusTimer);
    status.textContent = message;
    status.classList.toggle('error', error);
    status.hidden = !message;
    if (!persistent && message) statusTimer = setTimeout(() => status.hidden = true, error ? 6500 : 2800);
  };
  const open = id => {
    const el = $(id);
    if (!el) return;
    el.hidden = false;
    el.querySelector('input:not([type=hidden]), select')?.focus();
  };
  const close = id => {
    const el = $(id);
    if (el) el.hidden = true;
    if (id === 'restaurant-item-dialog') itemFeedback('');
  };
  // Feedback is displayed INSIDE the Add Item dialog, not behind its overlay.
  const itemFeedback = (message, error = false) => {
    const area = $('r-inv-item-feedback');
    if (!area) return;
    area.textContent = message;
    area.hidden = !message;
    area.classList.toggle('is-error', error);
  };
  document.addEventListener('click', event => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const dismiss = target.closest('[data-close]');
    if (dismiss) close(dismiss.dataset.close);
    const categoryEdit = target.closest('.restaurant-cat-edit');
    if (categoryEdit) {
      $('restaurant-category-form')?.reset();
      $('rc-id').value = categoryEdit.dataset.id || '';
      $('rc-name').value = categoryEdit.dataset.name || '';
      $('rc-printer').value = categoryEdit.dataset.printer || '';
      $('rc-order').value = categoryEdit.dataset.order || '0';
      $('rc-show').checked = ['yes', 'true'].includes((categoryEdit.dataset.show || '').toLowerCase());
      const title = $('restaurant-category-title');
      if (title) title.textContent = 'Edit Category';
      open('restaurant-category-dialog');
    }
    const itemEdit = target.closest('.restaurant-item-edit');
    if (itemEdit) {
      itemFeedback('');
      $('restaurant-item-form')?.reset();
      $('ri-barcode').value = itemEdit.dataset.barcode || '';
      $('ri-name').value = itemEdit.dataset.name || '';
      $('ri-category').value = itemEdit.dataset.category || '';
      $('ri-price').value = itemEdit.dataset.price || '0';
      $('ri-cost').value = itemEdit.dataset.cost || '0';
      const title = $('restaurant-item-title');
      if (title) title.textContent = 'Edit Menu Item';
      open('restaurant-item-dialog');
    }
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape')
      document.querySelectorAll('.restaurant-dialog:not([hidden])').forEach(el => close(el.id));
  });
  window.restaurantCategoryOpen = () => {
    $('restaurant-category-form')?.reset();
    $('rc-id').value = '';
    $('rc-show').checked = true;
    const title = $('restaurant-category-title');
    if (title) title.textContent = 'Add Category';
    open('restaurant-category-dialog');
  };
  window.restaurantItemOpen = () => {
    itemFeedback('');
    $('restaurant-item-form')?.reset();
    $('ri-barcode').value = '';
    const title = $('restaurant-item-title');
    if (title) title.textContent = 'Add Menu Item';
    open('restaurant-item-dialog');
  };

  const itemSearch = $('restaurant-inventory-search');
  const categorySearch = $('restaurant-category-search');
  const itemCategory = $('restaurant-inventory-category');
  const clearFilters = $('r-inv-clear-filters');
  const showing = $('r-inv-showing');
  const section = root.dataset.section === 'items' ? 'items' : 'categories';
  const activeTable = $(section === 'items' ? 'restaurant-items-table' : 'restaurant-categories-table');
  const singular = section === 'items' ? 'item' : 'category';
  const plural = section === 'items' ? 'items' : 'categories';
  const filter = () => {
    if (!activeTable) return;
    const search = (section === 'items' ? itemSearch?.value : categorySearch?.value) || '';
    const text = search.trim().toLocaleLowerCase();
    const category = section === 'items' ? (itemCategory?.value || '') : '';
    const tbody = activeTable.tBodies[0];
    if (!tbody) return;
    const rows = tbody.querySelectorAll('tr[data-search]');
    let matched = 0;
    rows.forEach(row => {
      const visible = (!text || (row.dataset.search || '').includes(text)) &&
        (!category || row.dataset.category === category);
      row.hidden = !visible;
      if (visible) matched++;
    });
    const filtered = !!(text || category);
    if (clearFilters) clearFilters.hidden = !filtered;
    if (showing) showing.textContent = filtered
      ? `${matched} of ${rows.length} ${plural}`
      : `${rows.length} ${rows.length === 1 ? singular : plural}`;

    // The empty state is local; filtering does not issue a server request.
    let empty = tbody.querySelector('.r-inv-no-results');
    if (rows.length && filtered && !matched) {
      if (!empty) {
        empty = document.createElement('tr');
        empty.className = 'r-inv-no-results';
        const cell = document.createElement('td');
        cell.colSpan = section === 'items' ? 6 : 5;
        cell.className = 'restaurant-empty';
        cell.textContent = 'No matching results. Try another search or clear the filters.';
        empty.appendChild(cell);
        tbody.appendChild(empty);
      }
      empty.hidden = false;
    } else if (empty) {
      empty.hidden = true;
    }
  };
  let frame = 0;
  const queueFilter = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(filter);
  };
  itemSearch?.addEventListener('input', queueFilter);
  categorySearch?.addEventListener('input', queueFilter);
  itemCategory?.addEventListener('change', filter);
  clearFilters?.addEventListener('click', () => {
    if (itemSearch) itemSearch.value = '';
    if (categorySearch) categorySearch.value = '';
    if (itemCategory) itemCategory.value = '';
    filter();
    (itemSearch || categorySearch)?.focus();
  });
  filter();

  async function refreshRows() {
    const section = root.dataset.section === 'items' ? 'items' : 'categories';
    const url = new URL(root.dataset.rowsUrl, location.origin);
    url.searchParams.set('section', section);
    const response = await fetch(url, {
      credentials: 'same-origin', cache: 'no-store',
      headers: { 'X-Requested-With': 'XMLHttpRequest', Accept: 'text/html' }
    });
    if (!response.ok) throw new Error('Inventory was saved, but the updated list could not be loaded. Refresh the page.');
    const table = $(section === 'items' ? 'restaurant-items-table' : 'restaurant-categories-table');
    const tbody = table?.querySelector('tbody');
    if (!tbody) throw new Error('Inventory list is not available. Reload the page.');
    tbody.innerHTML = await response.text();
    filter();
    const count = tbody.querySelectorAll('tr[data-search]').length;
    const tab = root.querySelector('.restaurant-tabs a.active .restaurant-count');
    if (tab) tab.textContent = String(count);
  }

  // Forms return JSON for async requests. Never retry a successful write after
  // a later refresh error; e.g. this prevents duplicate new menu items.
  document.addEventListener('submit', async event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const action = new URL(form.action, location.origin).pathname.toLowerCase();
    if (!['/restaurant/savecategory','/restaurant/deletecategory',
           '/restaurant/saveitem','/restaurant/deleteitem'].some(path => action.endsWith(path))) return;
    event.preventDefault();
    if (busy || !form.reportValidity()) return;
    if (form.dataset.confirm && !window.confirm(form.dataset.confirm)) return;
    busy = true;
    const submit = form.querySelector('button[type=submit]');
    const label = submit?.textContent;
    const addingItem = form.id === 'restaurant-item-form' && !($('ri-barcode')?.value || '').trim();
    const selectedCategory = addingItem ? ($('ri-category')?.value || '') : '';
    if (addingItem) itemFeedback('');
    if (submit) { submit.disabled = true; submit.textContent = 'Saving…'; }
    root.classList.add('r-inventory-busy');
    if (!addingItem) notify('Saving inventory…', false, true);
    let saved = false;
    try {
      const response = await fetch(form.action, {
        method: 'POST', body: new FormData(form), credentials: 'same-origin',
        headers: { 'X-Requested-With': 'XMLHttpRequest', Accept: 'application/json' }
      });
      const result = await response.json();
      if (!response.ok || !result.ok) throw new Error(result.message || 'Could not save inventory.');
      saved = true;
      if (addingItem) {
        // Keep the Add Item popup open for the next item. Never repeat the POST.
        // Reset first so an interrupted grid refresh cannot cause a duplicate.
        form.reset();
        $('ri-barcode').value = '';
        $('ri-category').value = selectedCategory;
        $('ri-name')?.focus();
        try {
          await refreshRows();
          itemFeedback('Item added successfully. You can add another item.');
        } catch (refreshError) {
          itemFeedback('Item saved, but the grid could not refresh. Reload the page to see the new item.', true);
        }
      } else {
        close('restaurant-category-dialog');
        close('restaurant-item-dialog');
        await refreshRows();
        notify(result.message || 'Inventory updated.');
      }
    } catch (error) {
      const message = saved
        ? 'Saved successfully, but the list could not refresh. Reload Inventory.'
        : (error.message || 'Request failed. Check whether it saved before trying again.');
      if (addingItem) itemFeedback(message, true);
      else notify(message, true, true);
    } finally {
      busy = false;
      root.classList.remove('r-inventory-busy');
      if (submit && submit.isConnected) { submit.disabled = false; submit.textContent = label; }
    }
  });
})();
