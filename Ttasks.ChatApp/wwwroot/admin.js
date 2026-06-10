// ttasks admin — shared JS helpers used by all admin pages.
window.tt = {
  escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, ch => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[ch]));
  },
  fmtTime(value) {
    if (!value) return '';
    try { return new Date(value).toLocaleString(); }
    catch { return String(value); }
  },
  pill(status) {
    const safe = this.escapeHtml(status || 'Unknown');
    return `<span class="pill status-${safe}">${safe}</span>`;
  },
  kindBadge(kind) {
    return `<span class="kind-badge">${this.escapeHtml(kind)}</span>`;
  },
  shortId(value, len = 8) {
    return String(value || '').slice(0, len);
  },
};
