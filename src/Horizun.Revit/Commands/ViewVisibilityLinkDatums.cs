// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_view_visibility operation=link_datum_cleanup (§6, field report 2026-11-17, Revit 2024).
//
// Each link drew its grids and levels over the host's (up to 14 identical bubbles). The API has
// no per-category Custom table for links and HideElements takes host ids only, so this does the
// one thing measured to work: in OUR view_id, per datum kind, a ParameterFilterElement on Grids
// (or Levels) whose rule is "Type Name not equal to" EVERY host type of the kind (drawn today or
// not, so a host datum shown again later is never swallowed), added to the view and set INVISIBLE.
// Host datums keep their names and stay; linked datums of other type names are hidden.
//
// Where the filter goes (5.2.1): the view itself (it must be ours); for a view whose Filters come
// from a template, a COPY made with its detailing (duplicate_view: the template released and its
// settings applied once; the original may be somebody else's, the copy is ours and the original is
// never touched) or the template itself (edit_template: the template and EVERY view that uses it
// must pass the restricted-tool ownership gate, HrzOwnership; the users are bound into the token).
//
// Honest about what it cannot do, per linked datum: same type name as a host type, a link whose
// display does not take the host's filters (By linked view; Custom with its own filters on 2025+;
// not readable on 2023 and for Custom on 2024). Datums of NESTED links are read (one bounded walk
// per call) but never counted as hidden: whether Revit applies the host filter to them is not measured.
// Rehearse by default, single-use token, re-read: the filter, its categories and rules, its place
// and visibility in the view, the rule VALUES, and that every host type of the kind is among them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        private sealed class DatumFilterPlan
        {
            public string Kind, Name;
            /// <summary>true: "Name not equal to" the host datum names (opt-in name_separator); false: "Type Name not equal to" the host types.</summary>
            public bool ByName;
            public ElementId Category, Parameter, ExistingId;
            public List<string> HostNames;
            /// <summary>Linked datums of TOP-LEVEL links this filter hides (a datum is counted by one filter only); unresolved ones.</summary>
            public int LinkedHidden, LinkedUnresolved;
        }

        private sealed class LinkedSource
        {
            public Document Doc; public long LinkId; public int Depth; public string Path;
            /// <summary>Takes the host view's filters: the link's own answer, or for a nested one the prediction from its parent.</summary>
            public bool? Takes;
        }

        private static string DocKey(Document d) => (d.PathName ?? "") + "|" + d.Title;

        /// <summary>
        /// The linked document of <paramref name="top"/> and every document nested inside it, read under ONE budget for the whole call
        /// (depth and document count; a nested document read under another top link is not read again). Read only.
        /// </summary>
        private static List<LinkedSource> LinkedSources(RevitLinkInstance top, Document ld, bool? topTakes, bool? nestedTakes,
                                                        LinkDatumFilterRules.NestedBudget budget)
        {
            budget.AddTop(DocKey(ld));
            var chain0 = new List<KeyValuePair<string, long>> { new KeyValuePair<string, long>(ld.Title, Rid.Value(top.Id)) };
            var list = new List<LinkedSource> { new LinkedSource { Doc = ld, LinkId = Rid.Value(top.Id), Depth = 0, Path = LinkDatumFilterRules.LinkPath(chain0), Takes = topTakes } };
            var chains = new List<List<KeyValuePair<string, long>>> { chain0 };
            for (int i = 0; i < list.Count; i++)
            {
                LinkedSource cur = list[i];
                if (cur.Depth >= LinkDatumFilterRules.MaxNestedDepth) continue;
                IList<RevitLinkInstance> inner;
                try { inner = new FilteredElementCollector(cur.Doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(); }
                catch { continue; }
                foreach (RevitLinkInstance n in inner)
                {
                    Document nd = null;
                    try { nd = n.GetLinkDocument(); } catch { }
                    if (nd == null || !budget.TryAdd(DocKey(nd))) continue;
                    var chain = new List<KeyValuePair<string, long>>(chains[i]) { new KeyValuePair<string, long>(nd.Title, Rid.Value(n.Id)) };
                    chains.Add(chain);
                    list.Add(new LinkedSource { Doc = nd, LinkId = Rid.Value(top.Id), Depth = cur.Depth + 1, Path = LinkDatumFilterRules.LinkPath(chain), Takes = nestedTakes });
                }
            }
            return list;
        }

        /// <summary>The values of a "Type Name not equal to" filter on <paramref name="parameter"/>; null when a rule has another shape.</summary>
        private static List<string> RuleValues(ParameterFilterElement pf, ElementId parameter)
        {
            try
            {
                if (!(pf?.GetElementFilter() is ElementParameterFilter epf)) return null;
                var values = new List<string>();
                foreach (FilterRule rule in epf.GetRules())
                {
                    // Every shape of "not equal to" Revit may hand back: an inverted equals string rule (2023-2027, measured
                    // by the absence of FilterStringNotEquals in their RevitAPI.dll) or a string rule with a not-equals
                    // evaluator, should a year add one. Matched by type NAME so no year needs a type it lacks.
                    FilterRule inner = rule is FilterInverseRule inv ? inv.GetInnerRule() : rule;
                    if (!(inner is FilterStringRule sr)) return null;
                    string shape = LinkDatumFilterRules.RuleShape(rule is FilterInverseRule, inner.GetType().Name, sr.GetEvaluator()?.GetType().Name);
                    if (shape != LinkDatumFilterRules.NotEqualsShape || !sr.GetRuleParameter().Equals(parameter)) return null;
                    values.Add(sr.RuleString);
                }
                return values;
            }
            catch { return null; }
        }

        /// <summary>A candidate separator's value on a datum of <paramref name="d"/> (host or link); null when unreadable.</summary>
        private static string SeparatorValue(Document d, Element e, string candidate)
        {
            try
            {
                switch (candidate)
                {
                    case LinkDatumSeparators.Name: return e.Name;
                    case LinkDatumSeparators.Workset:
                        return d.IsWorkshared ? d.GetWorksetTable().GetWorkset(e.WorksetId)?.Name : null;
                    case LinkDatumSeparators.ScopeBox:
                        Parameter p = e.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST);
                        return p == null ? null : LinkDatumSeparators.ScopeBoxName(p.AsValueString() ?? p.AsString());
                }
            }
            catch { }
            return null;
        }

        private static LinkDatumSeparators.Row SeparatorRow(Document d, Element e)
        {
            var row = new LinkDatumSeparators.Row { Id = Rid.Value(e.Id).ToString() };
            foreach (string c in LinkDatumSeparators.Candidates) row.Values[c] = SeparatorValue(d, e, c);
            return row;
        }

        /// <summary>The links nested inside <paramref name="ld"/>: the instance AND its type, because showing one needs both. Read only.</summary>
        private static JObject NestedLinksOf(RevitLinkInstance outer, Document ld)
        {
            var nested = new JArray();
            foreach (RevitLinkInstance n in new FilteredElementCollector(ld).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                string typeName = null, status = null;
                long? typeId = null;
                try
                {
                    var t = ld.GetElement(n.GetTypeId()) as RevitLinkType;
                    typeName = t?.Name; typeId = t == null ? (long?)null : Rid.Value(t.Id);
                    status = t?.GetLinkedFileStatus().ToString();
                }
                catch { }
                nested.Add(new JObject { ["instance_id"] = Rid.Value(n.Id), ["instance_name"] = n.Name, ["link_type_id"] = typeId.HasValue ? (JToken)typeId.Value : JValue.CreateNull(),
                    ["link_type_name"] = typeName, ["status"] = status });
            }
            return new JObject { ["link_instance_id"] = Rid.Value(outer.Id), ["name"] = outer.Name, ["nested"] = nested };
        }

        /// <summary>Every sheet each view is placed on ("number - name"), by view id. Read only.</summary>
        private static Dictionary<long, List<string>> SheetsByView(Document doc)
        {
            var map = new Dictionary<long, List<string>>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try
                {
                    var sheet = doc.GetElement(vp.SheetId) as ViewSheet;
                    if (sheet == null) continue;
                    long v = Rid.Value(vp.ViewId);
                    if (!map.TryGetValue(v, out List<string> l)) map[v] = l = new List<string>();
                    l.Add(sheet.SheetNumber + " - " + sheet.Name);
                }
                catch { }
            }
            return map;
        }

        /// <summary>A view's filters with their state (visibility and enabled), by filter id.</summary>
        private static Dictionary<long, string> FilterStates(View v)
        {
            var map = new Dictionary<long, string>();
            if (v == null) return map;
            foreach (ElementId f in v.GetFilters())
            {
                bool? enabled = null;
                try { enabled = v.GetIsFilterEnabled(f); } catch { }
                map[Rid.Value(f)] = LinkDatumFilterRules.FilterState(v.GetFilterVisibility(f), enabled);
            }
            return map;
        }

        /// <summary>
        /// How <paramref name="v"/> is drawn, for the settings duplicate_view compares: each one read from <paramref name="template"/>
        /// when the template controls it (that is what the view shows), else from the view.
        /// </summary>
        private static LinkDatumFilterRules.ViewLook LookOf(Document d, View v, View template)
        {
            View From(params BuiltInParameter[] ps) => template != null && ps.Any(p => ManageViewsCommand.TemplateGoverns(template, p)) ? template : v;
            var look = new LinkDatumFilterRules.ViewLook();
            try { look.Scale = From(BuiltInParameter.VIEW_SCALE, BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL).Scale; } catch { }
            try { look.DetailLevel = From(BuiltInParameter.VIEW_DETAIL_LEVEL).DetailLevel.ToString(); } catch { }
            try { look.Filters = FilterStates(From(BuiltInParameter.VIS_GRAPHICS_FILTERS)); } catch { look.Filters = null; }
            foreach (Category c in d.Settings.Categories)
            {
                try
                {
                    View src = c.CategoryType == CategoryType.Model ? From(BuiltInParameter.VIS_GRAPHICS_MODEL)
                             : c.CategoryType == CategoryType.Annotation ? From(BuiltInParameter.VIS_GRAPHICS_ANNOTATION)
                             : c.CategoryType == CategoryType.AnalyticalModel ? From(BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL) : v;
                    if (src.CanCategoryBeHidden(c.Id) && src.GetCategoryHidden(c.Id)) look.HiddenCategories.Add(Rid.Value(c.Id));
                }
                catch { }
            }
            return look;
        }

        private static ArchModelEdit LinkDatumCleanup(Document doc, JObject r, View view, HashSet<string> session)
        {
            List<string> kinds = ViewDatumRules.ParseKinds(r["datum_kinds"], out string why);
            string tmode = LinkDatumFilterRules.ParseTemplateMode(r["template_mode"], out string tmodeError);
            if (tmode == null) throw new ArgumentException(tmodeError);
            bool nameSeparator = r.Value<bool?>("name_separator") ?? false;
            ManageViewsCommand.RequireOverridesAllowed(view, "filters");
            View template = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId) as View;
            bool governed = ManageViewsCommand.TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_FILTERS);
            if (governed && tmode == LinkDatumFilterRules.TemplateRefuse)
                throw new ArgumentException(LinkDatumFilterRules.TemplateRefusal(view.Name, template.Name, Rid.Value(template.Id)));
            bool makeCopy = governed && tmode == LinkDatumFilterRules.TemplateDuplicateView;
            bool onTemplate = governed && tmode == LinkDatumFilterRules.TemplateEdit;
            if (onTemplate && nameSeparator) throw new ArgumentException(LinkDatumFilterRules.NameSeparatorOnTemplateRefusal);
            // Written in place: the view must be ours. duplicate_view writes only on the copy it makes (ours), never on the original.
            if (!governed) RequireOwn(doc, view, session);
            Dictionary<long, List<string>> sheets = SheetsByView(doc);

            View target = view;
            var templateUsers = new JArray();
            var userViews = new List<View>();
            if (onTemplate)
            {
                // The restricted tools' gate (view_crop, sheet_layout): this user's mark; a version 1 mark only in a model that is not
                // collaborative; or created in this session. On the template AND on every view that uses it, because all of them change.
                string templateWhy = HrzOwnership.GateReason(doc, template);
                if (templateWhy != null)
                    throw new ArgumentException(LinkDatumFilterRules.ForeignTemplateRefusal(template.Name, Rid.Value(template.Id), templateWhy));
                var foreign = new List<string>();
                int foreignTotal = 0;
                foreach (View other in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                             .Where(o => !o.IsTemplate && o.ViewTemplateId.Equals(template.Id)).OrderBy(o => Rid.Value(o.Id)))
                {
                    userViews.Add(other);
                    string uwhy = HrzOwnership.GateReason(doc, other);
                    if (uwhy != null) { foreignTotal++; if (foreign.Count < 20) foreign.Add("'" + other.Name + "' (" + Rid.Value(other.Id) + ")"); }
                    if (templateUsers.Count < LinkDatumFilterRules.MaxListedTemplateUsers)
                        templateUsers.Add(new JObject
                        {
                            ["view_id"] = Rid.Value(other.Id), ["name"] = other.Name,
                            ["sheets"] = new JArray(sheets.TryGetValue(Rid.Value(other.Id), out List<string> on) ? on : new List<string>()),
                            ["ours"] = uwhy == null
                        });
                }
                if (foreignTotal > 0)
                    throw new ArgumentException(LinkDatumFilterRules.ForeignTemplateUsersRefusal(template.Name, Rid.Value(template.Id), userViews.Count, foreign, foreignTotal));
                target = template;
            }
            long nameId = Rid.Value(target.Id);

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().OrderBy(l => Rid.Value(l.Id)).ToList();
            var linkRows = new JArray();
            var takes = new Dictionary<long, bool?>();
            var nestedTakes = new Dictionary<long, bool?>();
            foreach (RevitLinkInstance l in links)
            {
                string vis = null, vf = null, nestedSetting = null;
                bool readable = LinkSettingsReadable;
#if !REVIT2023
                try
                {
                    RevitLinkGraphicsSettings g = view.GetLinkOverrides(l.Id);
                    if (g != null)
                    {
                        vis = g.LinkVisibilityType.ToString();
#if REVIT2025 || REVIT2026 || REVIT2027
                        vf = g.ViewFilterType.ToString();
                        try { nestedSetting = g.NestedLinks.ToString(); } catch { }
#endif
                    }
                }
                catch { readable = false; }
#endif
                bool? t = readable ? LinkDatumFilterRules.TakesHostFilters(vis, vf) : null;
                bool? nt = LinkDatumFilterRules.NestedTakesHostFilters(t, vis, nestedSetting);
                takes[Rid.Value(l.Id)] = t;
                nestedTakes[Rid.Value(l.Id)] = nt;
                linkRows.Add(new JObject { ["link_instance_id"] = Rid.Value(l.Id), ["name"] = l.Name, ["display"] = vis ?? (readable ? "ByHostView" : null),
                    ["view_filters"] = vf, ["takes_host_filters"] = t.HasValue ? (JToken)t.Value : JValue.CreateNull(),
                    ["nested_links_display"] = nestedSetting,
                    ["nested_take_host_filters_predicted"] = nt.HasValue ? (JToken)nt.Value : JValue.CreateNull() });
            }

            // Every linked document, nested ones included, read once under one budget for the whole call.
            var sources = new List<LinkedSource>();
            var unavailable = new SortedSet<long>();
            var budget = new LinkDatumFilterRules.NestedBudget();
            foreach (RevitLinkInstance l in links)
            {
                Document ld = null;
                try { ld = l.GetLinkDocument(); } catch { }
                if (ld == null) { unavailable.Add(Rid.Value(l.Id)); continue; }
                sources.AddRange(LinkedSources(l, ld, takes[Rid.Value(l.Id)], nestedTakes[Rid.Value(l.Id)], budget));
            }

            var plans = new List<DatumFilterPlan>();
            var unresolved = new JArray();
            int unresolvedTotal = 0;
            int nested = 0, linkedTotal = 0;
            var nestedRows = new JArray();
            bool nestedTruncated = false;
            var nestedCounts = new JObject();
            var separators = new JArray();
            var hostVisibleBefore = new List<ElementId>();
            var skipped = new JArray();
            var unprotected = new JArray();
            var assumptions = new List<JObject>();
            foreach (string kind in kinds)
            {
                bool grid = kind == ViewDatumRules.KindGrid;
                Type cls = grid ? typeof(Grid) : typeof(Level);
                var sameNameRows = new List<LinkDatumSeparators.Row>();
                var plan = new DatumFilterPlan { Kind = kind, Name = LinkDatumFilterRules.FilterName(kind, nameId),
                    Category = new ElementId(grid ? BuiltInCategory.OST_Grids : BuiltInCategory.OST_Levels) };
                if (!onTemplate)
                    foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(cls).WhereElementIsNotElementType())
                    {
                        bool hidden; try { hidden = e.IsHidden(view); } catch { hidden = false; }
                        if (!hidden) hostVisibleBefore.Add(e.Id);
                    }
                // EVERY host type of the kind, drawn today or not: a host datum hidden or outside the crop now must not be
                // swallowed by the filter when somebody shows it again (review B3).
                plan.HostNames = LinkDatumFilterRules.HostNames(new FilteredElementCollector(doc).OfClass(grid ? typeof(GridType) : typeof(LevelType)).Select(t => t.Name));
                var hostDatums = new FilteredElementCollector(doc).OfClass(cls).WhereElementIsNotElementType().ToList();
                foreach (Element e in hostDatums)
                {
                    string tn = null;
                    try { tn = doc.GetElement(e.GetTypeId())?.Name; } catch { }
                    string reason = LinkDatumFilterRules.HostUnprotected(tn, plan.HostNames);
                    if (reason != null && unprotected.Count < 200)
                        unprotected.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["name"] = e.Name, ["kind"] = kind, ["type_name"] = tn, ["reason"] = reason });
                }
                if (plan.HostNames.Count > LinkDatumFilterRules.MaxNames)
                    throw new ArgumentException("the host has " + plan.HostNames.Count + " " + kind + " types in use; a filter takes at most " + LinkDatumFilterRules.MaxNames + " rules.");
                ICollection<ElementId> filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, new List<ElementId> { plan.Category });
                foreach (BuiltInParameter bip in new[] { BuiltInParameter.SYMBOL_NAME_PARAM, BuiltInParameter.ALL_MODEL_TYPE_NAME })
                    if (plan.Parameter == null && filterable.Contains(new ElementId(bip))) plan.Parameter = new ElementId(bip);

                // The optional second separator: the datum NAME. It hides a linked datum whose name no host datum has.
                bool nameFilterable = filterable.Contains(new ElementId(BuiltInParameter.DATUM_TEXT));
                DatumFilterPlan namePlan = null;
                List<string> hostDatumNames = null;
                if (nameSeparator)
                {
                    hostDatumNames = nameFilterable ? LinkDatumFilterRules.HostDatumNames(hostDatums.Select(e => e.Name)) : null;
                    if (!nameFilterable) skipped.Add(new JObject { ["kind"] = kind, ["reason"] = "name_not_filterable", ["mode"] = "name" });
                    else if (hostDatumNames == null) skipped.Add(new JObject { ["kind"] = kind, ["reason"] = "host_datum_names_unsafe", ["mode"] = "name",
                        ["advice"] = "the host has no " + kind + ", or one of its names is empty or has edge spaces, so a Name rule could not keep it" });
                    else if (hostDatumNames.Count > LinkDatumFilterRules.MaxNames) skipped.Add(new JObject { ["kind"] = kind, ["reason"] = "too_many_host_names", ["mode"] = "name" });
                    else
                    {
                        namePlan = new DatumFilterPlan { Kind = kind, ByName = true, Name = LinkDatumFilterRules.NameFilterName(kind, nameId),
                            Category = plan.Category, Parameter = new ElementId(BuiltInParameter.DATUM_TEXT), HostNames = hostDatumNames };
                    }
                }

                int nestedTotal = 0, nestedByType = 0, nestedByName = 0, nestedUnresolved = 0;
                foreach (LinkedSource src in sources)
                {
                    Document ld = src.Doc;
                    bool isNested = src.Depth > 0;
                    if (kind == kinds[0] && src.Depth == 0 && new FilteredElementCollector(ld).OfClass(typeof(RevitLinkInstance)).GetElementCount() > 0)
                    {
                        nested++;
                        RevitLinkInstance outer = links.First(x => Rid.Value(x.Id) == src.LinkId);
                        if (nestedRows.Count < 50) nestedRows.Add(NestedLinksOf(outer, ld)); else nestedTruncated = true;
                    }
                    foreach (Element e in new FilteredElementCollector(ld).OfClass(cls).WhereElementIsNotElementType())
                    {
                        string typeName = null;
                        try { typeName = ld.GetElement(e.GetTypeId())?.Name; } catch { }
                        bool? tk = src.Takes;
                        string reason = plan.Parameter == null ? "type_name_not_filterable" : LinkDatumFilterRules.Unresolved(typeName, plan.HostNames, tk);
                        // The name rule works on the link only when its display takes the host's filters.
                        bool byName = namePlan != null && tk == true && LinkDatumFilterRules.NameSeparates(e.Name, hostDatumNames);
                        LinkDatumFilterRules.DatumOutcome outcome = LinkDatumFilterRules.Classify(reason, byName);
                        if (isNested)
                        {
                            // NOT counted as hidden and never deciding no_op: whether Revit applies a host filter to a nested link is not measured.
                            nestedTotal++;
                            if (outcome == LinkDatumFilterRules.DatumOutcome.ByType) nestedByType++;
                            else if (outcome == LinkDatumFilterRules.DatumOutcome.ByNameOnly) nestedByName++;
                            else nestedUnresolved++;
                        }
                        else
                        {
                            linkedTotal++;
                            if (outcome == LinkDatumFilterRules.DatumOutcome.ByType) { plan.LinkedHidden++; continue; }
                            if (outcome == LinkDatumFilterRules.DatumOutcome.ByNameOnly) { namePlan.LinkedHidden++; continue; }
                            plan.LinkedUnresolved++;
                            if (reason == LinkDatumFilterRules.SameName) sameNameRows.Add(SeparatorRow(ld, e));
                        }
                        if (outcome != LinkDatumFilterRules.DatumOutcome.Unresolved) continue;
                        unresolvedTotal++;
                        if (unresolved.Count < 200)
                            unresolved.Add(new JObject { ["link_instance_id"] = src.LinkId, ["linked_element_id"] = Rid.Value(e.Id), ["name"] = e.Name,
                                ["kind"] = kind, ["type_name"] = typeName, ["reason"] = reason, ["nested_depth"] = src.Depth,
                                ["link_path"] = src.Path, ["measured"] = !isNested,
                                ["advice"] = LinkDatumFilterRules.Advice(reason) ?? "Revit offers no type-name filter on this category in this version" });
                    }
                }
                nestedCounts[kind] = new JObject
                {
                    ["datums"] = nestedTotal, ["would_hide_by_type"] = nestedByType, ["would_hide_by_name"] = nestedByName, ["unresolved"] = nestedUnresolved,
                    ["measured"] = false
                };
                if (sameNameRows.Count > 0)
                {
                    // Can ANOTHER parameter tell these apart from the host's? Computed over EVERY host datum of the kind.
                    var hostRows = hostDatums.Select(e => SeparatorRow(doc, e)).ToList();
                    separators.Add(LinkDatumSeparators.ToJson(kind, LinkDatumSeparators.Analyze(hostRows, sameNameRows, null, nameFilterable), sameNameRows.Count, nameFilterable));
                }
                if (!makeCopy)
                {
                    foreach (DatumFilterPlan fp in new[] { plan, namePlan })
                    {
                        if (fp == null) continue;
                        // Revit filter names are unique without regard to case.
                        string fpName = fp.Name;
                        Element existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).FirstOrDefault(f => string.Equals(f.Name, fpName, StringComparison.OrdinalIgnoreCase))
                                           ?? new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).FirstOrDefault(f => string.Equals(f.Name, fpName, StringComparison.OrdinalIgnoreCase));
                        if (existing != null && !(existing is ParameterFilterElement))
                            throw new ArgumentException("a filter named '" + existing.Name + "' already exists and is not a rule filter; rename it.");
                        if (existing != null)
                        {
                            var users = new List<string>();
                            foreach (View other in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                            {
                                bool uses = false;
                                try { uses = other.GetFilters().Contains(existing.Id); } catch { }
                                if (LinkDatumFilterRules.OtherFilterUser(Rid.Value(other.Id), Rid.Value(other.ViewTemplateId), uses, Rid.Value(target.Id), target.IsTemplate))
                                    users.Add((other.IsTemplate ? "template '" : "view '") + other.Name + "' (" + Rid.Value(other.Id) + ")");
                            }
                            if (users.Count > 0)
                                throw new ArgumentException("filter '" + existing.Name + "' (" + Rid.Value(existing.Id) + ") is also used by " + string.Join(", ", users.Take(10)) +
                                    (users.Count > 10 ? " and " + (users.Count - 10) + " more" : "") + "; rewriting it would change those too. Remove it there or rename it.");
                        }
                        fp.ExistingId = existing?.Id;
                    }
                }
                if (plan.Parameter == null) skipped.Add(new JObject { ["kind"] = kind, ["reason"] = "type_name_not_filterable" });
                else if (plan.HostNames.Count == 0)
                    skipped.Add(new JObject { ["kind"] = kind, ["reason"] = LinkDatumFilterRules.NoHostNames, ["advice"] = LinkDatumFilterRules.Advice(LinkDatumFilterRules.NoHostNames) });
                else { plans.Add(plan); assumptions.Add(LinkDatumFilterRules.TypeFilterAssumption(kind)); }
                if (namePlan != null && namePlan.LinkedHidden > 0) { plans.Add(namePlan); assumptions.Add(LinkDatumFilterRules.NameFilterAssumption(kind)); }
                else if (namePlan != null) skipped.Add(new JObject { ["kind"] = kind, ["mode"] = "name", ["reason"] = "no_linked_datum_needs_the_name_rule",
                    ["advice"] = "every linked " + kind + " the name rule could hide is already hidden by the type rule, or none has a name the host lacks" });
            }

            int hiddenTotal = plans.Sum(p => p.LinkedHidden);
            var summary = new JObject
            {
                ["operation"] = "link_datum_cleanup", ["view_id"] = Rid.Value(view.Id), ["view_name"] = view.Name,
                ["filters"] = new JArray(plans.Select(p =>
                {
                    var f = new JObject { ["kind"] = p.Kind, ["mode"] = p.ByName ? "name" : "type",
                        ["name"] = makeCopy ? "chosen at apply from the copy's id" : p.Name,
                        ["reuses_filter_id"] = p.ExistingId == null ? null : (JToken)Rid.Value(p.ExistingId),
                        ["rule"] = p.ByName ? "Name not equal to each of" : "Type Name not equal to each of" };
                    f[p.ByName ? "host_datum_names" : "host_type_names"] = new JArray(p.HostNames);
                    f["linked_hidden"] = p.LinkedHidden;
                    if (!p.ByName) f["linked_unresolved"] = p.LinkedUnresolved;
                    else f["counts"] = "only the linked datums the type rule does not hide already";
                    return f;
                })),
                ["linked_hidden_total"] = hiddenTotal,
                ["links"] = linkRows, ["linked_datums"] = linkedTotal, ["unresolved"] = unresolved, ["unresolved_total"] = unresolvedTotal,
                ["unresolved_truncated"] = unresolvedTotal > unresolved.Count,
                ["unavailable_link_instances"] = new JArray(unavailable.Select(i => (JToken)i)),
                ["links_with_nested_links"] = nested, ["nested_links"] = nestedRows, ["nested_links_truncated"] = nestedTruncated,
                ["linked_documents_read"] = budget.Documents, ["linked_documents_cap"] = LinkDatumFilterRules.MaxNestedDocuments,
                ["nested_documents_scan_truncated"] = budget.Truncated,
                ["nested_link_datums_not_measured"] = nestedCounts,
                ["same_type_name_separators"] = separators,
                ["skipped_kinds"] = skipped, ["host_datums_unprotected"] = unprotected,
                ["template_mode"] = tmode,
                ["not_covered"] = "datums of NESTED links are read and listed in nested_link_datums_not_measured, but they are not counted in linked_hidden and never decide " +
                                  "the outcome: whether Revit applies a host view filter to them was not measured (the prediction from the parent link's display is in links[]." +
                                  "nested_take_host_filters_predicted). Showing or hiding a nested link instance still needs its instance AND its RevitLinkType"
            };
            if (!governed && tmode != LinkDatumFilterRules.TemplateRefuse)
                summary["template_mode_note"] = "the view's filters are not controlled by a template, so template_mode changes nothing: the filter goes on the view itself";
            List<string> originalSheets = sheets.TryGetValue(Rid.Value(view.Id), out List<string> os) ? os : new List<string>();
            if (governed)
            {
                var t = new JObject
                {
                    ["template_id"] = Rid.Value(template.Id), ["template_name"] = template.Name, ["mode"] = tmode,
                    ["where"] = makeCopy ? "a NEW view (a copy of this one, with its detailing) in the project browser; the original view and the template are not changed"
                                         : "the template itself: every view that uses it changes"
                };
                if (onTemplate)
                {
                    t["views_using_template"] = templateUsers;
                    t["views_using_template_total"] = userViews.Count;
                    t["views_using_template_truncated"] = userViews.Count > templateUsers.Count;
                    t["owner_check"] = "the template and every view that uses it passed the ownership gate of view_crop and sheet_layout; the list is bound to the confirmation token";
                }
                summary["template"] = t;
            }
            if (makeCopy)
            {
                summary["copy"] = new JObject
                {
                    ["name"] = LinkDatumFilterRules.CopyNameAtApply(view.Name),
                    ["placed_on_a_sheet"] = false,
                    ["original_on_sheets"] = new JArray(originalSheets),
                    ["template"] = "none: template '" + template.Name + "' is released on the copy and its settings are applied to it once",
                    ["detailing"] = "copied with the view (ViewDuplicateOption.WithDetailing)",
                    ["compared_after_apply"] = "scale, detail level, filters with their visibility, hidden categories (against the original as drawn)"
                };
                assumptions.InsertRange(0, LinkDatumFilterRules.DuplicateViewAssumptions(template.Name, originalSheets));
            }
            if (plans.Count == 0 || hiddenTotal == 0)
            {
                // Nothing will be created or applied, so no assumption about a filter or a copy is in play.
                summary["state"] = "no_op";
                summary["means"] = links.Count == 0 ? "this model has no Revit links; nothing was written"
                                                    : "no datum of a top-level link can be hidden by these filters here (see unresolved; nested links are not counted); nothing was written";
                return new ArchModelEdit { ReadResult = summary };
            }
            AssumptionNotes.Stamp(summary, assumptions);

            var edit = new ArchModelEdit { Summary = summary };
            if (makeCopy)
            {
                // The original is only READ: its state is bound to the token, and the re-read proves it unchanged.
                PlannedElement src = ModelEditRunner.Planned(view, PlannedAction.Read, r);
                src.BeforeValues["view_template_id"] = Rid.Value(template.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
                edit.Planned.Add(src);
                edit.Planned.Add(new PlannedElement
                {
                    UniqueId = "create:linked-datum-view-copy", Category = "Views", Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string> { { "source_view", Rid.Value(view.Id).ToString() }, { "template", Rid.Value(template.Id).ToString() } },
                    ProposedValues = new Dictionary<string, string> { { "request", ArchitecturalEditRules.ProposedRequest(r) } }
                });
            }
            else
            {
                edit.Planned.Add(ModelEditRunner.Planned(target, PlannedAction.Modify, r));
                foreach (DatumFilterPlan p in plans.Where(p => p.ExistingId != null))
                    edit.Planned.Add(ModelEditRunner.Planned(doc.GetElement(p.ExistingId), PlannedAction.Modify, r));
                // edit_template: EVERY view that uses the template changes, so each one is bound to the token (a view that starts or
                // stops using it between the rehearsal and the apply makes the plan stale).
                foreach (View u in userViews)
                {
                    PlannedElement pe = ModelEditRunner.Planned(u, PlannedAction.Read, r);
                    pe.BeforeValues["view_template_id"] = Rid.Value(u.ViewTemplateId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    edit.Planned.Add(pe);
                }
            }
            var hostIds = hostVisibleBefore.ToList();
            long originalTemplate = template == null ? -1 : Rid.Value(template.Id);
            Dictionary<long, string> originalFilters = new Dictionary<long, string>();
            try { originalFilters = FilterStates(view); } catch { originalFilters = null; }
            LinkDatumFilterRules.ViewLook originalLook = makeCopy ? LookOf(doc, view, template) : null;
            var userTemplates = userViews.ToDictionary(u => Rid.Value(u.Id), u => Rid.Value(u.ViewTemplateId));
            // Per apply, never on the plan: a retried apply starts again from these (the copy's id and name are only known then).
            ElementId workId = null;
            var filterIds = new Dictionary<DatumFilterPlan, ElementId>();
            var filterNames = new Dictionary<DatumFilterPlan, string>();
            edit.Apply = d =>
            {
                filterIds.Clear(); filterNames.Clear();
                View v;
                if (makeCopy)
                {
                    View src = (View)d.GetElement(view.Id);
                    ElementId dupId = src.Duplicate(ViewDuplicateOption.WithDetailing);
                    View dup = (View)d.GetElement(dupId);
                    dup.Name = LinkDatumFilterRules.CopyName(src.Name, new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().Select(x => x.Name));
                    View tpl = (View)d.GetElement(template.Id);
                    dup.ViewTemplateId = ElementId.InvalidElementId;
                    // "Apply template properties" once: what the template controlled is written into the copy itself.
                    dup.ApplyViewTemplateParameters(tpl);
                    v = dup; workId = dupId;
                    foreach (DatumFilterPlan p in plans)
                        filterNames[p] = p.ByName ? LinkDatumFilterRules.NameFilterName(p.Kind, Rid.Value(dupId)) : LinkDatumFilterRules.FilterName(p.Kind, Rid.Value(dupId));
                }
                else
                {
                    v = (View)d.GetElement(target.Id); workId = target.Id;
                    foreach (DatumFilterPlan p in plans) filterNames[p] = p.Name;
                }
                foreach (DatumFilterPlan p in plans)
                {
                    IList<FilterRule> rules = p.HostNames.Select(n => ParameterFilterRuleFactory.CreateNotEqualsRule(p.Parameter, n)).ToList();
                    ElementFilter rule = new ElementParameterFilter(rules);
                    var cats = new List<ElementId> { p.Category };
                    ParameterFilterElement pf;
                    if (!makeCopy && p.ExistingId != null)
                    {
                        pf = (ParameterFilterElement)d.GetElement(p.ExistingId);
                        pf.SetCategories(cats);
                        pf.SetElementFilter(rule);
                    }
                    else pf = ParameterFilterElement.Create(d, filterNames[p], cats, rule);
                    filterIds[p] = pf.Id;
                    if (!v.GetFilters().Contains(pf.Id)) v.AddFilter(pf.Id);
                    v.SetFilterVisibility(pf.Id, false);
                    try { v.SetIsFilterEnabled(pf.Id, true); } catch { }
                    // Read the rules back INSIDE the transaction: a shape not recognised (unreadable) or other values throws
                    // here, so the runner rolls the edit back before any commit instead of failing after it.
                    List<string> values = RuleValues(pf, p.Parameter);
                    string bad = LinkDatumFilterRules.RulesReadBackError(values, p.HostNames);
                    if (bad != null) throw new InvalidOperationException("filter '" + filterNames[p] + "': " + bad + " Rolled back before commit.");
                }
            };
            edit.Verify = d =>
            {
                View v = workId == null ? null : d.GetElement(workId) as View;
                var names = new List<string>();
                foreach (DatumFilterPlan p in plans)
                {
                    string k = p.Kind + (p.ByName ? ":name" : "");
                    names.AddRange(new[] { "filter:" + k, "in_view_hidden:" + k, "rules:" + k, "host_protected:" + k });
                }
                if (makeCopy) names.AddRange(new[] { "copy_exists", "copy_template_released", "copy_looks_like_original", "original_template_kept", "original_filters_unchanged" });
                if (onTemplate) names.Add("template_users_unchanged");
                var check = new PostconditionCheck(names.ToArray());
                var ours = new HashSet<long>(filterIds.Values.Select(Rid.Value));
                if (makeCopy)
                {
                    View orig = d.GetElement(view.Id) as View;
                    check.Compare("copy_exists", true, v != null && !v.Id.Equals(view.Id));
                    check.Compare("copy_template_released", true, v != null && v.ViewTemplateId == ElementId.InvalidElementId);
                    // The copy against the original AS DRAWN (template-controlled settings read from the template): scale, detail level,
                    // filters with their visibility (ours excluded) and hidden categories. What is not compared is a SUPUESTO in the reply.
                    View tplNow = orig == null ? null : d.GetElement(orig.ViewTemplateId) as View;
                    List<string> diffs = LinkDatumFilterRules.LookDifferences(orig == null ? null : LookOf(d, orig, tplNow), v == null ? null : LookOf(d, v, null), ours);
                    check.Record("copy_looks_like_original", new JArray(), new JArray(diffs), diffs.Count == 0);
                    check.Compare("original_template_kept", originalTemplate, orig == null ? -1 : Rid.Value(orig.ViewTemplateId));
                    // WHICH filters the original has and the visibility of each, not only how many.
                    List<string> fdiff = originalFilters == null || orig == null ? new List<string> { "unreadable" }
                                       : LinkDatumFilterRules.FilterDifferences(originalFilters, FilterStates(orig));
                    check.Record("original_filters_unchanged", new JArray(), new JArray(fdiff), fdiff.Count == 0);
                    edit.Evidence["original_look_before"] = originalLook == null ? null : new JObject
                    {
                        ["scale"] = originalLook.Scale, ["detail_level"] = originalLook.DetailLevel,
                        ["filters"] = originalLook.Filters == null ? null : JObject.FromObject(originalLook.Filters.ToDictionary(k => k.Key.ToString(), k => k.Value)),
                        ["hidden_categories"] = originalLook.HiddenCategories.Count
                    };
                    edit.Evidence["copy"] = new JObject
                    {
                        ["placed_on_a_sheet"] = false, ["original_on_sheets"] = new JArray(originalSheets),
                        ["template"] = "none (released; '" + template.Name + "' applied once)"
                    };
                }
                if (onTemplate)
                {
                    var moved = new JArray();
                    foreach (var kv in userTemplates)
                    {
                        var u = d.GetElement(Rid.Make(kv.Key)) as View;
                        long now = u == null ? -1 : Rid.Value(u.ViewTemplateId);
                        if (now != kv.Value) moved.Add(kv.Key);
                    }
                    check.Record("template_users_unchanged", new JArray(), moved, moved.Count == 0);
                }
                var filters = new JArray();
                foreach (DatumFilterPlan p in plans)
                {
                    string k = p.Kind + (p.ByName ? ":name" : "");
                    var pf = filterIds.TryGetValue(p, out ElementId fid) ? d.GetElement(fid) as ParameterFilterElement : null;
                    var cats = pf?.GetCategories();
                    string wantName = filterNames.TryGetValue(p, out string fn) ? fn : p.Name;
                    check.Compare("filter:" + k, true, pf != null && pf.Name == wantName && cats.Count == 1 && cats.Contains(p.Category));
                    bool inView = pf != null && v != null && v.GetFilters().Contains(pf.Id);
                    bool hidden = inView && !v.GetFilterVisibility(pf.Id);
                    check.Compare("in_view_hidden:" + k, true, hidden);
                    // The rule VALUES, not only their count: each must be a "not equal" on the type name (or the datum name).
                    List<string> values = RuleValues(pf, p.Parameter);
                    bool same = values != null && LinkDatumFilterRules.SameNames(values, p.HostNames);
                    check.Record("rules:" + k, new JArray(p.HostNames), values == null ? (JToken)"unreadable" : new JArray(values), same);
                    // Every host type (or host datum name), re-read now, is among the values: the filter cannot hide a host datum.
                    List<string> hostNow = p.ByName
                        ? new FilteredElementCollector(d).OfClass(p.Kind == ViewDatumRules.KindGrid ? typeof(Grid) : typeof(Level)).WhereElementIsNotElementType().Select(e => e.Name).ToList()
                        : new FilteredElementCollector(d).OfClass(p.Kind == ViewDatumRules.KindGrid ? typeof(GridType) : typeof(LevelType)).Select(t => t.Name).ToList();
                    var missing = values == null ? hostNow : hostNow.Where(n => !values.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                    check.Record("host_protected:" + k, new JArray(), new JArray(missing), missing.Count == 0);
                    filters.Add(new JObject { ["kind"] = p.Kind, ["mode"] = p.ByName ? "name" : "type", ["filter_id"] = pf == null ? null : (JToken)Rid.Value(pf.Id), ["name"] = wantName });
                }
                edit.Evidence["working_view"] = new JObject { ["view_id"] = workId == null ? null : (JToken)Rid.Value(workId), ["name"] = v?.Name, ["is_copy"] = makeCopy, ["is_template"] = v != null && v.IsTemplate };
                // A view TEMPLATE cannot be collected by view id ("no way of representing drawn elements"; measured on Revit 2026): its
                // evidence is the views that use it, read by horizun_manage_links (Pro edition) visible_elements.
                bool vIsTemplate = v != null && v.IsTemplate;
                var after = vIsTemplate || v == null ? new HashSet<long>() : new HashSet<long>(new FilteredElementCollector(d, v.Id).WhereElementIsNotElementType().ToElementIds().Select(Rid.Value));
                // NOT a postcondition: whether FilteredElementCollector(doc, viewId) applies view FILTERS is neither
                // documented nor measured, so this count proves nothing either way. Evidence, labelled unmeasured.
                edit.Evidence["host_datums_still_visible"] = new JObject
                {
                    ["status"] = "unmeasured", ["before"] = hostIds.Count, ["collected_after"] = vIsTemplate ? (JToken)"not_collectable_on_a_template" : hostIds.Count(id => after.Contains(Rid.Value(id))),
                    ["why"] = "the view collector is not known to apply view filters; host_protected is the check"
                };
                edit.Evidence["filters"] = filters;
                edit.Evidence["unresolved"] = unresolved;
                edit.Evidence["links"] = linkRows;
                edit.Evidence["nested_link_datums_not_measured"] = nestedCounts;
                // Verify runs twice (inside the group and after it): the rows are written once, not appended again.
                edit.Evidence.Remove("assumptions"); edit.Evidence.Remove("has_assumptions");
                AssumptionNotes.Stamp(edit.Evidence, assumptions.Select(a => (JObject)a.DeepClone()));
#if !REVIT2023
                var measured = new JObject();
                foreach (RevitLinkInstance l in links)
                    measured[Rid.Value(l.Id).ToString()] = vIsTemplate || v == null
                        ? new JObject { ["measured"] = false, ["reason"] = "target_is_a_view_template", ["how"] = "read a view that uses the template with horizun_manage_links (Pro edition) visible_elements" }
                        : VisibleByCategory(d, v, l, new HashSet<string> { "OST_Grids", "OST_Levels" });
                edit.Evidence["linked_datums_visible_after"] = measured;
#else
                edit.Evidence["linked_datums_visible_after"] = "not measurable on Revit 2023 (the link-in-view collector is 2024+)";
#endif
                return check;
            };
            return edit;
        }
    }
}
