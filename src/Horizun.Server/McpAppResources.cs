// -----------------------------------------------------------------------------
// Horizun MCP - the clash viewer as an MCP App. Original Horizun code.
//
// G05 of the 2026-09-14 competitive inventory: "visor interactivo en el chat ...
// mismo resultado en ambos modos". The last clause is the hard part and the one
// this file is built around.
//
// AN MCP APP MUST NOT BE A SECOND SOURCE OF TRUTH. A viewer that fetches its own
// data, or computes anything the textual reply did not say, produces a second
// answer to the same question - and the two will disagree on the day it matters.
// So:
//
//   * the app renders ONLY what the tool result already contains. It has no
//     network access, loads nothing from a CDN, and asks the server for nothing.
//     What it draws is what a reader of the text would have read.
//
//   * Text() renders the SAME payload to plain text, from the same fields, in the
//     same order. The equivalence is therefore assertable: a test feeds one
//     payload to both and compares what each says about every clash. "Same result
//     in both modes" stops being a promise and becomes a property.
//
//   * a host that does not support MCP Apps loses nothing. The textual result is
//     the result; the app is a rendering of it.
//
// WHAT THE APP CAN DO BEYOND DISPLAY. One thing: ask the host to call
// horizun_navigate for the element the user clicked, which selects it in Revit.
// That is a request the host may refuse, and the row still shows both element ids
// either way - so the identity a coordinator needs is never behind a button.
//
// HOST/LINK IDENTITY IS NOT DECORATION. A clash between element 12345 of the
// host model and element 12345 of a linked model is two different pairs, and a
// viewer that shows only the numbers has lost the finding. Every row names the
// document each side belongs to, and a side whose document could not be read
// says so rather than defaulting to the host.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpAppResources
    {
        public const string ClashViewerUri = "ui://horizun/clash-viewer";

        /// <summary>The mime type the MCP Apps extension names for an app resource.</summary>
        public const string AppMimeType = "text/html;profile=mcp-app";

        /// <summary>
        /// The resource descriptor for resources/list.
        ///
        /// `_meta.ui.csp` is deliberately the most restrictive thing that still renders:
        /// no external origins at all. This app has no reason to reach the network, and a
        /// policy that allows one is a policy somebody will eventually use.
        /// </summary>
        public static JObject Definition() => new JObject
        {
            ["uri"] = ClashViewerUri,
            ["name"] = "clash-viewer",
            ["title"] = "Clash viewer",
            ["description"] =
                "An interactive view of a horizun_clash result. It renders only what the tool result already " +
                "contains and fetches nothing, so it cannot disagree with the text.",
            ["mimeType"] = AppMimeType,
            ["size"] = Encoding.UTF8.GetByteCount(Html()),
            ["annotations"] = new JObject
            {
                ["audience"] = new JArray("user"),
                ["priority"] = 0.6
            },
            ["_meta"] = new JObject
            {
                ["io.modelcontextprotocol/ui"] = new JObject
                {
                    ["csp"] = new JObject
                    {
                        ["connect-src"] = new JArray(),
                        ["img-src"] = new JArray(),
                        ["style-src"] = new JArray(),
                        ["script-src"] = new JArray()
                    },
                    ["permissions"] = new JArray()
                }
            }
        };

        /// <summary>The `_meta.ui` block a tool carries to declare its app.</summary>
        public static JObject ToolUiMeta() => new JObject
        {
            ["ui"] = new JObject { ["resourceUri"] = ClashViewerUri }
        };

        /// <summary>
        /// The textual rendering of a clash payload.
        ///
        /// SAME FIELDS, SAME ORDER as the app. This exists so the equivalence can be
        /// tested rather than asserted in a comment, and so a host with no MCP Apps
        /// support gets exactly the finding the viewer would have shown.
        /// </summary>
        public static string Text(JObject payload)
        {
            var lines = new List<string>();
            JArray clashes = Rows(payload);

            lines.Add("Clashes: " + clashes.Count);
            string coverage = payload?.Value<string>("coverage") ?? payload?.Value<string>("means");
            if (!string.IsNullOrWhiteSpace(coverage)) lines.Add("Coverage: " + coverage);

            int index = 0;
            foreach (JToken token in clashes)
            {
                var row = token as JObject;
                if (row == null) continue;
                index++;
                Side a = SideOf(row, "a");
                Side b = SideOf(row, "b");
                lines.Add(
                    index + ". " + a.Describe() + "  x  " + b.Describe() +
                    "  [" + (row.Value<string>("kind") ?? "intersection") + "]" +
                    Overlap(row));
            }
            if (index == 0) lines.Add("(no clashes in the measured scope - which is not the same as none in the model)");
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>The clash rows, wherever the payload happens to keep them.</summary>
        private static JArray Rows(JObject payload)
        {
            foreach (string key in new[] { "clashes", "results", "rows", "findings" })
            {
                if (payload?[key] is JArray array) return array;
            }
            return new JArray();
        }

        private static string Overlap(JObject row)
        {
            JToken overlap = row["overlap"] ?? row["overlap_volume"] ?? row["distance"];
            return overlap == null || overlap.Type == JTokenType.Null
                ? ""
                : "  overlap=" + overlap.ToString(Newtonsoft.Json.Formatting.None);
        }

        private struct Side
        {
            public string Id;
            public string LinkInstanceId;
            public string Document;
            public string Category;

            public string Describe()
            {
                string document = string.IsNullOrWhiteSpace(Document) ? "document unknown" : Document;
                string category = string.IsNullOrWhiteSpace(Category) ? "" : " " + Category;
                return (string.IsNullOrWhiteSpace(Id) ? "?" : Id) + category + " (" + document + ")";
            }
        }

        /// <summary>
        /// One side of a clash. A side whose document could not be read reports
        /// "document unknown" rather than silently reading as the host model: two
        /// elements with the same id in two documents are a different finding.
        /// </summary>
        private static Side SideOf(JObject row, string side)
        {
            JObject nested = row[side] as JObject;
            return new Side
            {
                Id = Value(nested, row, side, "element_id", "id"),
                // THE LINK INSTANCE IS PART OF THE IDENTITY, not decoration beside it. An
                // element id with no document means nothing: two documents can hold the same
                // number, and the viewer used to send one of them to the host and select
                // whatever answered.
                LinkInstanceId = Value(nested, row, side, "link_instance_id"),
                Document = Value(nested, row, side, "document", "document_title"),
                Category = Value(nested, row, side, "category", "category_name")
            };
        }

        private static string Value(JObject nested, JObject row, string side, params string[] names)
        {
            foreach (string name in names)
            {
                JToken token = nested?[name] ?? row[side + "_" + name];
                if (token != null && token.Type != JTokenType.Null) return token.ToString();
            }
            return null;
        }

        /// <summary>
        /// The app itself. One file, no dependencies, no network.
        ///
        /// The postMessage dialect is implemented by hand rather than through the
        /// extension's SDK for the same reason the MCP wire format is: a bridge whose
        /// rendering depends on a package it cannot audit is a bridge that ships
        /// somebody else's failure modes.
        /// </summary>
        public static string Html()
        {
            return @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Clash viewer</title>
<style>
  :root { color-scheme: light dark; }
  body { margin: 0; font: 13px/1.45 ui-sans-serif, system-ui, sans-serif; }
  header { padding: 10px 12px; border-bottom: 1px solid rgba(128,128,128,.35); }
  h1 { margin: 0; font-size: 14px; font-weight: 600; }
  .coverage { margin-top: 4px; opacity: .75; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; padding: 6px 12px; border-bottom: 1px solid rgba(128,128,128,.2); vertical-align: top; }
  th { font-weight: 600; opacity: .75; }
  tr.row:hover { background: rgba(128,128,128,.12); cursor: pointer; }
  tr.row.selected { background: rgba(80,140,255,.18); }
  .doc { opacity: .7; }
  .unknown { opacity: .7; font-style: italic; }
  .empty { padding: 16px 12px; opacity: .8; }
  footer { padding: 8px 12px; opacity: .7; border-top: 1px solid rgba(128,128,128,.35); }
  .status { padding: 6px 12px; border-bottom: 1px solid rgba(128,128,128,.2); }
  .status.waiting { opacity: .7; }
  .status.busy { background: rgba(80,140,255,.12); }
  .status.failed { background: rgba(255,90,90,.16); }
  .status.partial { background: rgba(255,180,60,.16); }
  .status.hidden { display: none; }
</style>
</head>
<body>
<header>
  <h1 id=""title"">Clashes</h1>
  <div class=""coverage"" id=""coverage""></div>
</header>
<div class=""status waiting"" id=""status"">Waiting for the clash result.</div>
<div id=""body""></div>
<footer id=""footer""></footer>
<script>
(function () {
  'use strict';

  // The app NEVER fetches. Everything it shows arrived with the tool result, so
  // it cannot disagree with the text the same result printed.
  // `pending` maps a JSON-RPC id to what it was for, so a reply can be attributed.
  // Without it a refused selection and an honoured one look identical: the
  // coordinator clicks, Revit does nothing visible, and they conclude the element
  // is gone.
  var state = { payload: null, selected: -1, canCall: false, pending: {}, status: null };
  var nextId = 1;

  function setStatus(kind, text) {
    state.status = { kind: kind, text: text };
    var node = document.getElementById('status');
    if (!node) return;
    node.className = 'status ' + (kind || 'hidden');
    node.textContent = text || '';
  }

  function post(method, params, describe) {
    var id = nextId++;
    if (describe) state.pending[id] = describe;
    parent.postMessage({ jsonrpc: '2.0', id: id, method: method, params: params || {} }, '*');
    return id;
  }

  function notify(method, params) {
    parent.postMessage({ jsonrpc: '2.0', method: method, params: params || {} }, '*');
  }

  function rows(payload) {
    if (!payload) return [];
    var keys = ['clashes', 'results', 'rows', 'findings'];
    for (var i = 0; i < keys.length; i++) {
      if (Array.isArray(payload[keys[i]])) return payload[keys[i]];
    }
    return [];
  }

  function value(row, side, names) {
    var nested = row[side];
    for (var i = 0; i < names.length; i++) {
      if (nested && nested[names[i]] !== undefined && nested[names[i]] !== null) return String(nested[names[i]]);
      var flat = side + '_' + names[i];
      if (row[flat] !== undefined && row[flat] !== null) return String(row[flat]);
    }
    return null;
  }

  function escapeHtml(text) {
    return String(text === null || text === undefined ? '' : text)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/""/g, '&quot;');
  }

  function sideCell(row, side) {
    var id = value(row, side, ['element_id', 'id']);
    var link = value(row, side, ['link_instance_id']);
    var doc = value(row, side, ['document', 'document_title']);
    var category = value(row, side, ['category', 'category_name']);
    // The document is part of the identity. Two elements with the same id in two
    // documents are a different finding, so an unreadable document says so rather
    // than quietly reading as the host model.
    var docHtml = doc
      ? '<div class=""doc"">' + escapeHtml(doc) + '</div>'
      : '<div class=""doc unknown"">document unknown</div>';
    // The link instance is SHOWN, not just used: a reader comparing this against a
    // model needs to know which of two same-numbered elements they are looking at,
    // and the number alone cannot tell them.
    var linkHtml = link
      ? '<div class=""doc"">in link ' + escapeHtml(link) + '</div>'
      : '';
    return '<td>' + escapeHtml(id || '?') +
           (category ? ' <span class=""doc"">' + escapeHtml(category) + '</span>' : '') +
           docHtml + linkHtml + '</td>';
  }

  function render() {
    var payload = state.payload;
    var list = rows(payload);
    var coverage = payload && (payload.coverage || payload.means);

    document.getElementById('title').textContent = 'Clashes: ' + list.length;
    document.getElementById('coverage').textContent = coverage || '';

    if (!list.length) {
      document.getElementById('body').innerHTML =
        '<div class=""empty"">No clashes in the measured scope. That is not the same as none in the model: ' +
        'read the coverage statement above.</div>';
      return;
    }

    var html = '<table><thead><tr><th>#</th><th>A</th><th>B</th><th>Kind</th><th>Overlap</th></tr></thead><tbody>';
    for (var i = 0; i < list.length; i++) {
      var row = list[i];
      var overlap = row.overlap !== undefined ? row.overlap
                  : (row.overlap_volume !== undefined ? row.overlap_volume : row.distance);
      html += '<tr class=""row"" data-index=""' + i + '"">' +
              '<td>' + (i + 1) + '</td>' +
              sideCell(row, 'a') + sideCell(row, 'b') +
              '<td>' + escapeHtml(row.kind || 'intersection') + '</td>' +
              '<td>' + escapeHtml(overlap === undefined || overlap === null ? '' : overlap) + '</td>' +
              '</tr>';
    }
    html += '</tbody></table>';
    document.getElementById('body').innerHTML = html;

    var trs = document.querySelectorAll('tr.row');
    for (var t = 0; t < trs.length; t++) trs[t].addEventListener('click', onSelect);

    // A RESULT THAT MEASURED PART OF THE MODEL LOOKS EXACTLY LIKE ONE THAT MEASURED
    // ALL OF IT, unless it is said. The payload's own coverage fields decide, and a
    // payload that says nothing about coverage gets no claim either way.
    if (!state.status || state.status.kind !== 'failed') {
      var complete = payload && payload.coverage_complete;
      if (complete === false) {
        setStatus('partial', 'PARTIAL: this result did not cover the whole scope. Rows missing from it ' +
                             'were not looked at, which is not the same as no clash being there.');
      } else if (state.status && state.status.kind === 'waiting') {
        setStatus('hidden', '');
      }
    }

    document.getElementById('footer').textContent = state.canCall
      ? 'Click a row to select BOTH sides of that clash in Revit, each with the link it lives in. ' +
        'Every id is shown either way.'
      : 'This host has not granted tool calls, so selection in Revit is unavailable. Every id is shown above.';
  }

  function onSelect(event) {
    var index = parseInt(event.currentTarget.getAttribute('data-index'), 10);
    var list = rows(state.payload);
    if (isNaN(index) || index < 0 || index >= list.length) return;

    state.selected = index;
    var trs = document.querySelectorAll('tr.row');
    for (var t = 0; t < trs.length; t++) trs[t].classList.toggle('selected', t === index);

    var row = list[index];

    // BOTH SIDES, EACH WHOLE. A clash is a pair: selecting one side of it and calling
    // that 'navigate to the clash' is half an answer that looks like a whole one. And
    // each side travels as (element_id, link_instance_id) rather than as a number -
    // this line used to send Number(id) as a host id, which for a host-to-link clash
    // selected whichever HOST element happened to carry the same number.
    var selections = [];
    var described = [];
    for (var side = 0; side < 2; side++) {
      var which = side === 0 ? 'a' : 'b';
      var rawId = value(row, which, ['element_id', 'id']);
      if (rawId === null || !/^-?[0-9]+$/.test(rawId)) continue;
      var entry = { element_id: parseInt(rawId, 10) };
      var rawLink = value(row, which, ['link_instance_id']);
      if (rawLink !== null && /^-?[0-9]+$/.test(rawLink)) {
        entry.link_instance_id = parseInt(rawLink, 10);
      }
      selections.push(entry);
      described.push(which.toUpperCase() + ' ' + rawId + (rawLink ? ' in link ' + rawLink : ''));
    }
    if (!selections.length) return;

    // A REQUEST, not a guarantee. The host may refuse it, and the row still shows
    // the ids - the finding is never behind a button.
    if (state.canCall) {
      setStatus('busy', 'Selecting ' + described.join(' and ') + ' in Revit…');
      post('tools/call', {
        name: 'horizun_navigate',
        arguments: { operation: 'select', selections: selections }
      }, 'select ' + described.join(' and '));
    } else {
      // NOT AN ERROR, AND NOT SILENCE. A host that grants no tool calls is a host
      // where selection is unavailable, and saying so beats a click that appears
      // to do nothing.
      setStatus('waiting', 'This host has not granted tool calls, so Revit was not asked to select ' +
                           'anything. The ids are in the row.');
    }
    notify('ui/context-update', {
      text: 'The clash viewer selected clash ' + (index + 1) + ' of ' + list.length +
            ' (' + described.join(' and ') + ').'
    });
  }

  window.addEventListener('message', function (event) {
    var message = event.data;
    if (!message || typeof message !== 'object') return;

    // The host hands the app its capabilities and then the tool result. Both
    // shapes are read defensively: a host that names them differently must not
    // leave the viewer blank with no explanation.
    var result = message.result || message.params || {};
    if (result.capabilities) state.canCall = !!(result.capabilities.tools || result.capabilities.toolCall);

    // A REPLY TO SOMETHING THIS APP ASKED FOR. Attributed by id, so a failure names
    // what failed instead of appearing as a click that did nothing.
    if (message.id !== undefined && state.pending[message.id]) {
      var what = state.pending[message.id];
      delete state.pending[message.id];
      if (message.error) {
        setStatus('failed', 'Revit refused: ' + what + '. ' +
                            (message.error.message || 'No reason was given.') +
                            ' The ids are still in the row; clicking again retries.');
      } else {
        setStatus('hidden', '');
      }
      return;
    }

    var payload = result.structuredContent || result.toolResult || result.data ||
                  (result.result && result.result.structuredContent);
    if (payload) { state.payload = payload; render(); }
  });

  post('ui/initialize', {
    protocolVersion: '2026-01-26',
    appInfo: { name: 'horizun-clash-viewer', version: '1' }
  });

  render();
})();
</script>
</body>
</html>";
        }
    }
}
