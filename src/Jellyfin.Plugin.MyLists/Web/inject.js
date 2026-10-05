// Injected into jellyfin-web (via File Transformation): adds "My Lists" to the sidebar. Safe to run repeatedly.
(function () {
  'use strict';
  var script = document.currentScript;
  var base = script ? new URL('./', script.src).href : '../MyLists/';
  var enabled = null;
  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true"><path fill="currentColor" d="M3 5h2v2H3zm4 0h14v2H7zM3 11h2v2H3zm4 0h14v2H7zM3 17h2v2H3zm4 0h14v2H7z"/></svg>';

  fetch(base + 'api/status').then(function (r) { return r.json(); }).then(function (s) {
    enabled = s.sidebar !== false;
    if (enabled) ensure();
  }).catch(function () { enabled = false; });

  function legacyDrawer() {
    var host = document.querySelector('.mainDrawer .customMenuOptions') || document.querySelector('.mainDrawer-scrollContainer');
    if (!host || host.querySelector('.mylists-link')) return;
    var a = document.createElement('a');
    a.setAttribute('is', 'emby-linkbutton');
    a.className = 'navMenuOption lnkMediaFolder mylists-link emby-button';
    a.href = base;
    a.innerHTML = '<span class="material-icons navMenuOptionIcon list" aria-hidden="true"></span><span class="sectionName navMenuOptionText">My Lists</span>';
    var home = host.querySelector('.lnkMediaFolder');
    if (host.classList.contains('customMenuOptions') || !home) host.appendChild(a);
    else home.parentNode.insertBefore(a, home.nextSibling);
  }

  // New MUI layout: clone a native drawer item so it looks identical.
  function muiDrawer() {
    var list = document.querySelector('.MuiDrawer-paper .MuiList-root');
    if (!list || list.querySelector('.mylists-link')) return;
    var items = list.querySelectorAll(':scope > li');
    var template = null;
    for (var i = items.length - 1; i >= 0; i--) {
      if (items[i].querySelector('a[href^="#/home"]')) { template = items[i]; break; }
    }
    var li;
    if (template) {
      li = template.cloneNode(true);
      var a = li.querySelector('a');
      a.href = base;
      a.classList.remove('Mui-selected');
      a.removeAttribute('aria-current');
      var icon = li.querySelector('.MuiListItemIcon-root');
      if (icon) icon.innerHTML = ICON;
      var text = li.querySelector('.MuiListItemText-primary');
      if (text) text.textContent = 'My Lists';
    } else {
      li = document.createElement('li');
      li.innerHTML = '<a href="' + base + '" style="display:flex;align-items:center;gap:32px;padding:8px 16px;color:inherit;text-decoration:none">' + ICON + '<span>My Lists</span></a>';
    }
    li.classList.add('mylists-link');
    list.appendChild(li);
  }

  var queued = false;
  function ensure() {
    if (!enabled || queued) return;
    queued = true;
    requestAnimationFrame(function () {
      queued = false;
      if (!window.ApiClient || !window.ApiClient.getCurrentUserId || !window.ApiClient.getCurrentUserId()) return;
      try { legacyDrawer(); muiDrawer(); } catch (e) { /* layout changed; ignore */ }
    });
  }

  // jellyfin-web's router treats clicks on drawer links as in-app routes and would turn our absolute URL into
  // "#/home/http://…". Intercept first (capture phase) and navigate for real.
  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest && e.target.closest('.mylists-link');
    if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    window.location.assign(base);
  }, true);

  new MutationObserver(ensure).observe(document.documentElement, { childList: true, subtree: true });
})();
