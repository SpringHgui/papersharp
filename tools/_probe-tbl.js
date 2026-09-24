(function () {
  function measure(txt, kind) {
    var d = document.createElement('div');
    d.textContent = txt;
    d.style.cssText = 'position:fixed;left:-99999px;top:0;font:10px/1 monospace;white-space:normal;width:' + kind + ';';
    document.body.appendChild(d);
    var w = d.getBoundingClientRect().width;
    d.remove();
    return w;
  }
  var out = [];
  document.querySelectorAll('td[data-mc]').forEach(function (td) {
    var n = +td.getAttribute('data-mc'), word = +(td.getAttribute('data-word') || 0);
    var ch = 'abcdefghijklmnopqrstuvwxyz';
    function run(len) { var s = ''; for (var i = 0; i < len; i++) s += ch[i % 26]; return s; }
    if (word) {
      var rest = Math.max(0, n - word);
      var parts = [run(word)];
      for (var i = 0; i < rest; i += 5) parts.push(run(Math.min(5, rest - i)));
      td.textContent = parts.join(' ');
    } else {
      var ps = [];
      for (var j = 0; j < n; j += 5) ps.push(run(Math.min(5, n - j)));
      td.textContent = ps.join(' ');
    }
  });
  document.body.offsetWidth;
  document.querySelectorAll('table').forEach(function (t) {
    var rows = [].slice.call(t.querySelectorAll('tr'));
    var ncol = rows[0].children.length;
    var used = [], mx = [], mn = [];
    for (var c = 0; c < ncol; c++) {
      var u = 0, a = 0, b = 0;
      rows.forEach(function (r) {
        var el = r.children[c];
        if (!el) return;
        u = Math.max(u, el.getBoundingClientRect().width);
        a = Math.max(a, measure(el.textContent, 'max-content'));
        b = Math.max(b, measure(el.textContent, 'min-content'));
      });
      used.push(+u.toFixed(2)); mx.push(+a.toFixed(2)); mn.push(+b.toFixed(2));
    }
    out.push({ id: t.id, tableW: +t.getBoundingClientRect().width.toFixed(2), used: used, maxC: mx, minC: mn });
  });
  return out;
})()
