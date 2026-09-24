// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_catalog_lookup operation=bsdd_*: READ-ONLY queries to the buildingSMART
// Data Dictionary. They share horizun_catalog_lookup (both look classification up)
// instead of adding a tool, because tools/list has a fixed size budget.
//
// bSDD is buildingSMART's public dictionary service: classification systems
// (Uniclass 2015, the IFC schema itself, national dictionaries...) published as
// classes with properties, allowed values, units and relations, each with a
// stable URI. Two uses in this bridge, both about pointing OUTWARD:
//
//   * mapping a project's own classification to a published one (search a
//     dictionary for the class, read its URI and relations);
//   * feeding the LOIN / IDS with STANDARD properties: a class's properties come
//     back already shaped as loin_property suggestions, with the bSDD URI that
//     an IDS property facet can carry.
//
// THE ENDPOINTS, as published by buildingSMART in bSDD OpenAPI.yaml
// (github.com/buildingSMART/bSDD, Documentation/, read 2026-09-24), all public
// GETs that need no authentication:
//
//   GET /api/TextSearch/v2          SearchText, DictionaryUris[], Offset, Limit
//   GET /api/SearchInDictionary/v1  DictionaryUri, SearchText, LanguageCode, RelatedIfcEntity, Offset, Limit
//   GET /api/Class/v1               Uri, IncludeClassProperties, IncludeClassRelations,
//                                   IncludeChildClassReferences, languageCode
//   GET /api/Property/v5            uri, languageCode       (v4 is deprecated)
//   GET /api/Dictionary/v1          Uri, Offset, Limit
//
// on https://api.bsdd.buildingsmart.org, always: the host is a constant, and the
// caller never supplies a URL that this process then fetches (URIs travel only
// as query parameters).
//
// WHAT THIS DOES NOT DO. It never writes to bSDD and never authenticates. It
// never turns a bSDD data type into an IFC one by guessing: bSDD says "Real",
// IFC says IfcLengthMeasure or IfcPositiveRatioMeasure, and only a person knows
// which. The suggestion says so instead of choosing.
//
// COST CONTROL. Answers are cached under %USERPROFILE%\.horizun\bsdd-cache with
// an expiry (default 7 days); a cached answer costs no call. Network calls are
// capped per minute and per process. Without network the reply says so plainly,
// and serves an EXPIRED cached copy only when one exists, labelled stale.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class BsddLookup
    {
        internal const string ToolName = "horizun_catalog_lookup";
        internal const string OperationPrefix = "bsdd_";
        internal const string ProductionBase = "https://api.bsdd.buildingsmart.org";
        internal const int MaxResponseBytes = 8 * 1024 * 1024;
        private const int MaxText = 1000;

        /// <summary>Injection points for tests; production uses the defaults.</summary>
        internal sealed class Options
        {
            public HttpMessageHandler Handler { get; set; }
            public string CacheDir { get; set; }
            public Func<DateTime> UtcNow { get; set; }
            public CallBudget Budget { get; set; }
        }

        /// <summary>
        /// The call cap: a sliding one-minute window and a per-process total. Cached answers
        /// are free. The numbers are deliberately modest: bSDD is a shared public service, and a
        /// loop that asks it the same thing thirty times a minute is a loop with a bug.
        /// </summary>
        internal sealed class CallBudget
        {
            private readonly object _gate = new object();
            private readonly Queue<DateTime> _recent = new Queue<DateTime>();
            private int _total;
            public int PerMinute { get; }
            public int PerProcess { get; }

            public CallBudget(int perMinute, int perProcess) { PerMinute = perMinute; PerProcess = perProcess; }

            /// <summary>null when a call may go out (and it is counted), otherwise why not.</summary>
            public string TryTake(DateTime now)
            {
                lock (_gate)
                {
                    while (_recent.Count > 0 && (now - _recent.Peek()).TotalSeconds >= 60) _recent.Dequeue();
                    if (_total >= PerProcess)
                        return "this server process has made " + _total + " bSDD calls, its cap. Restart the MCP client to " +
                               "reset it, and check for a loop asking the same question.";
                    if (_recent.Count >= PerMinute)
                    {
                        int wait = (int)Math.Ceiling(60 - (now - _recent.Peek()).TotalSeconds);
                        return PerMinute + " bSDD calls in the last minute is the cap; retry in about " + Math.Max(1, wait) + " s.";
                    }
                    _recent.Enqueue(now);
                    _total++;
                    return null;
                }
            }

            public int Used { get { lock (_gate) return _total; } }
        }

        private static readonly CallBudget DefaultBudget = new CallBudget(30, 500);
        private static readonly Lazy<HttpClient> DefaultClient = new Lazy<HttpClient>(() => NewClient(new HttpClientHandler()));

        private static HttpClient NewClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Horizun-Revit-MCP/1.0 (+https://horizunhub.com)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        internal static string DefaultCacheDir() => Path.Combine(HorizunPaths.DataRoot(), "bsdd-cache");

        internal static JObject Handle(JObject args, CancellationToken ct) => Handle(args, ct, null);

        internal static JObject Handle(JObject args, CancellationToken ct, Options options)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            options = options ?? new Options();
            string fullOperation = Str(args["operation"]);
            string operation = fullOperation != null && fullOperation.StartsWith(OperationPrefix, StringComparison.Ordinal)
                ? fullOperation.Substring(OperationPrefix.Length) : null;
            string baseUrl = ProductionBase;
            string language = Str(args["language_code"]);
            if (language != null && (language.Length > 16 || !language.All(c => char.IsLetterOrDigit(c) || c == '-')))
                throw new ToolRefusal("language_code looks like 'en-GB' or 'es-ES'; '" + language + "' is not one. Nothing was fetched.");
            int limit = Bounded(args, "limit", 20, 1, 100);
            int offset = Bounded(args, "offset", 0, 0, 100000);

            string path;
            var query = new List<KeyValuePair<string, string>>();
            switch (operation)
            {
                case "search":
                {
                    string text = RequireText(args, 2);
                    path = "/api/TextSearch/v2";
                    query.Add(Kv("SearchText", text));
                    foreach (string d in UriList(args, "dictionary_uris")) query.Add(Kv("DictionaryUris", d));
                    query.Add(Kv("Offset", offset.ToString(CultureInfo.InvariantCulture)));
                    query.Add(Kv("Limit", limit.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                case "search_dictionary":
                {
                    path = "/api/SearchInDictionary/v1";
                    query.Add(Kv("DictionaryUri", RequireUri(args, "uri")));
                    string text = Str(args["text"]);
                    if (text != null) query.Add(Kv("SearchText", CheckText(text, 1)));
                    string ifc = Str(args["related_ifc_entity"]);
                    if (ifc != null)
                    {
                        if (!ifc.All(char.IsLetterOrDigit) || ifc.Length > 64)
                            throw new ToolRefusal("related_ifc_entity is an IFC entity name such as IfcWall. Nothing was fetched.");
                        query.Add(Kv("RelatedIfcEntity", ifc));
                    }
                    if (language != null) query.Add(Kv("LanguageCode", language));
                    query.Add(Kv("Offset", offset.ToString(CultureInfo.InvariantCulture)));
                    query.Add(Kv("Limit", limit.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                case "class":
                    path = "/api/Class/v1";
                    query.Add(Kv("Uri", RequireUri(args, "uri")));
                    query.Add(Kv("IncludeClassProperties", "true"));
                    query.Add(Kv("IncludeClassRelations", "true"));
                    query.Add(Kv("IncludeChildClassReferences", "true"));
                    if (language != null) query.Add(Kv("languageCode", language));
                    break;
                case "property":
                    path = "/api/Property/v5";
                    query.Add(Kv("uri", RequireUri(args, "uri")));
                    if (language != null) query.Add(Kv("languageCode", language));
                    break;
                case "dictionaries":
                {
                    path = "/api/Dictionary/v1";
                    string uri = Str(args["uri"]);
                    if (uri != null) query.Add(Kv("Uri", RequireUri(args, "uri")));
                    query.Add(Kv("Offset", offset.ToString(CultureInfo.InvariantCulture)));
                    query.Add(Kv("Limit", limit.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                default:
                    throw new ToolRefusal("Unknown operation '" + fullOperation + "'. Use bsdd_search, bsdd_search_dictionary, " +
                                          "bsdd_class, bsdd_property or bsdd_dictionaries. Nothing was fetched.");
            }

            string url = baseUrl + path + "?" + string.Join("&", query.Select(q =>
                Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)));

            Fetched fetched = Fetch(url, args, ct, options);
            var result = new JObject
            {
                ["operation"] = fullOperation,
                ["request"] = new JObject { ["url"] = url, ["api"] = path },
                ["source"] = fetched.Source,
                ["fetched_utc"] = fetched.FetchedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["age_hours"] = Math.Round(fetched.AgeHours, 2),
                ["network_calls_this_process"] = (options.Budget ?? DefaultBudget).Used
            };
            if (fetched.Warning != null) result["warning"] = fetched.Warning;
            if (fetched.NotFound)
            {
                result["found"] = false;
                result["note"] = "bSDD answered 404: nothing is published at that URI.";
                return result;
            }

            JToken body = fetched.Body;
            switch (operation)
            {
                case "search": ShapeSearch(body, result); break;
                case "search_dictionary": ShapeSearchInDictionary(body, limit, result); break;
                case "class": ShapeClass(body, result); break;
                case "property": ShapeProperty(body, result); break;
                case "dictionaries": ShapeDictionaries(body, result); break;
            }
            result["found"] = true;
            return result;
        }

        // ============================================================================
        // Fetching, with the cache and the cap
        // ============================================================================

        private sealed class Fetched
        {
            public JToken Body;
            public bool NotFound;
            public string Source;         // network | cache | stale_cache
            public DateTime FetchedUtc;
            public double AgeHours;
            public string Warning;
        }

        private static Fetched Fetch(string url, JObject args, CancellationToken ct, Options options)
        {
            DateTime now = (options.UtcNow ?? (() => DateTime.UtcNow))();
            string dir = options.CacheDir ?? DefaultCacheDir();
            bool refresh = (bool?)args["refresh"] ?? false;
            double maxAge = Bounded(args, "max_age_hours", 168, 0, 2160);
            string file = Path.Combine(dir, ProjectContext.Sha256Hex(Encoding.UTF8.GetBytes(url)) + ".json");

            JObject cached = ReadCache(file, url);
            if (cached != null && !refresh)
            {
                DateTime at = (DateTime)cached["fetched_utc"];
                double age = (now - at).TotalHours;
                if (age <= maxAge)
                    return FromCache(cached, age, "cache", null);
            }

            string budgetRefusal = (options.Budget ?? DefaultBudget).TryTake(now);
            if (budgetRefusal != null)
            {
                if (cached != null)
                    return FromCache(cached, (now - (DateTime)cached["fetched_utc"]).TotalHours, "stale_cache",
                        "Call cap reached (" + budgetRefusal + ") - this is an EXPIRED cached answer, not a fresh one.");
                throw new ToolRefusal("bSDD call cap reached: " + budgetRefusal + " No cached answer exists for this query. Nothing was fetched.");
            }

            HttpClient owned = options.Handler != null ? NewClient(options.Handler) : null;
            HttpClient client = owned ?? DefaultClient.Value;
            HttpResponseMessage response;
            string text;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
                    long? declared = response.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > MaxResponseBytes)
                        throw new InvalidOperationException("bSDD answered " + declared.Value + " bytes for " + url +
                                                            "; the bound is " + MaxResponseBytes + ". Narrow the query (limit, dictionary).");
                    byte[] bytes = response.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
                    if (bytes.Length > MaxResponseBytes)
                        throw new InvalidOperationException("bSDD answered more than " + MaxResponseBytes + " bytes; narrow the query.");
                    text = new UTF8Encoding(false).GetString(bytes);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException)
            {
                string why = ex is OperationCanceledException ? "the request timed out" : ex.Message;
                if (cached != null)
                    return FromCache(cached, (now - (DateTime)cached["fetched_utc"]).TotalHours, "stale_cache",
                        "bSDD could not be reached (" + why + "). This is an EXPIRED cached answer from " +
                        (string)cached["fetched_utc"] + ", not a fresh one.");
                throw new InvalidOperationException(
                    "bSDD could not be reached (" + why + ") at " + url + ". This machine may be offline, or its DNS may " +
                    "not resolve api.bsdd.buildingsmart.org. No cached answer exists for this query, so nothing is " +
                    "reported: an unreachable dictionary is not an empty one.");
            }
            finally
            {
                owned?.Dispose();
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new Fetched { NotFound = true, Source = "network", FetchedUtc = now, AgeHours = 0 };
                if (!response.IsSuccessStatusCode)
                {
                    string snippet = text.Length > 400 ? text.Substring(0, 400) + "..." : text;
                    string what = (int)response.StatusCode == 429 ? "bSDD is rate-limiting this client (429)"
                        : (int)response.StatusCode >= 500 ? "bSDD failed on its side (" + (int)response.StatusCode + ")"
                        : "bSDD rejected the request (" + (int)response.StatusCode + ")";
                    if ((int)response.StatusCode >= 500 && cached != null)
                        return FromCache(cached, (now - (DateTime)cached["fetched_utc"]).TotalHours, "stale_cache",
                            what + ". This is an EXPIRED cached answer, not a fresh one.");
                    throw new InvalidOperationException(what + " for " + url + ": " + snippet);
                }
                JToken body;
                try
                {
                    using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                        body = JToken.ReadFrom(reader);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("bSDD answered 200 with a body that is not JSON (" + ex.Message +
                                                        ") for " + url + ". Nothing is reported from it.");
                }
                string warning = null;
                warning = WriteCache(dir, file, url, now, body);
                return new Fetched { Body = body, Source = "network", FetchedUtc = now, AgeHours = 0, Warning = warning };
            }
        }

        private static Fetched FromCache(JObject cached, double age, string source, string warning) => new Fetched
        {
            Body = cached["body"],
            NotFound = false,
            Source = source,
            FetchedUtc = (DateTime)cached["fetched_utc"],
            AgeHours = age,
            Warning = warning
        };

        private static JObject ReadCache(string file, string url)
        {
            try
            {
                if (!File.Exists(file)) return null;
                JObject entry;
                using (var reader = new JsonTextReader(new StreamReader(file, new UTF8Encoding(false))) { DateParseHandling = DateParseHandling.None })
                    entry = JObject.Load(reader);
                // A hash collision or a hand-edited file is not this query's answer.
                if ((string)entry["url"] != url || entry["body"] == null) return null;
                if (!DateTime.TryParse((string)entry["fetched_utc"], CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at)) return null;
                entry["fetched_utc"] = at;
                return entry;
            }
            catch { return null; }
        }

        /// <summary>Best effort: a cache that cannot be written costs a later call, never this answer.</summary>
        private static string WriteCache(string dir, string file, string url, DateTime now, JToken body)
        {
            string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(dir);
                var entry = new JObject
                {
                    ["url"] = url,
                    ["fetched_utc"] = now.ToString("o", CultureInfo.InvariantCulture),
                    ["body"] = body
                };
                File.WriteAllText(temp, entry.ToString(Formatting.None), new UTF8Encoding(false));
                File.Move(temp, file, true);
                return null;
            }
            catch (Exception ex)
            {
                return "The answer is fresh, but it could not be cached (" + ex.Message + "); the next identical query will call bSDD again.";
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        // ============================================================================
        // Shaping the replies
        // ============================================================================

        private static void ShapeSearch(JToken body, JObject result)
        {
            result["total_count"] = body?["totalCount"];
            result["offset"] = body?["offset"];
            result["count"] = body?["count"];
            result["classes"] = new JArray(Items(body?["classes"]).Select(c => new JObject
            {
                ["uri"] = S(c["uri"]),
                ["code"] = S(c["code"]),
                ["name"] = S(c["name"]),
                ["class_type"] = S(c["classType"]),
                ["dictionary_uri"] = S(c["dictionaryUri"]),
                ["dictionary_name"] = S(c["dictionaryName"]),
                ["status"] = S(c["status"]),
                ["definition"] = S(c["definition"]),
                ["related_ifc_entities"] = Strings(c["relatedIfcEntityNames"])
            }));
            result["properties"] = new JArray(Items(body?["properties"]).Select(p => new JObject
            {
                ["uri"] = S(p["uri"]),
                ["code"] = S(p["code"]),
                ["name"] = S(p["name"]),
                ["dictionary_uri"] = S(p["dictionaryUri"]),
                ["dictionary_name"] = S(p["dictionaryName"]),
                ["status"] = S(p["status"]),
                ["definition"] = S(p["definition"])
            }));
            result["next"] = "Read a class with operation=bsdd_class and its uri: its properties come back as loin_property suggestions.";
        }

        private static void ShapeSearchInDictionary(JToken body, int limit, JObject result)
        {
            JToken d = body?["dictionary"];
            result["dictionary"] = d == null ? null : new JObject
            {
                ["uri"] = S(d["dictionaryUri"]),
                ["name"] = S(d["dictionaryName"]),
                ["version"] = S(d["version"]),
                ["status"] = S(d["status"]),
                ["release_date"] = S(d["releaseDate"])
            };
            var flat = new JArray();
            bool truncated = false;
            void Walk(JToken items, string parent, int depth)
            {
                foreach (JToken c in Items(items))
                {
                    if (flat.Count >= Math.Max(limit, 1) * 5) { truncated = true; return; }
                    flat.Add(new JObject
                    {
                        ["uri"] = S(c["uri"]),
                        ["code"] = S(c["code"]),
                        ["name"] = S(c["name"]),
                        ["class_type"] = S(c["classType"]),
                        ["parent_code"] = S(c["parentClassCode"]) ?? parent,
                        ["depth"] = depth
                    });
                    if (depth < 12) Walk(c["children"], S(c["code"]), depth + 1);
                }
            }
            Walk(body?["classes"], null, 0);
            result["classes"] = flat;
            result["classes_truncated"] = truncated;
        }

        private static void ShapeClass(JToken c, JObject result)
        {
            result["class"] = new JObject
            {
                ["uri"] = S(c?["uri"]),
                ["code"] = S(c?["code"]),
                ["name"] = S(c?["name"]),
                ["class_type"] = S(c?["classType"]),
                ["status"] = S(c?["status"]),
                ["dictionary_uri"] = S(c?["dictionaryUri"]),
                ["definition"] = S(c?["definition"]),
                ["description"] = S(c?["description"]),
                ["reference_code"] = S(c?["referenceCode"]),
                ["related_ifc_entities"] = Strings(c?["relatedIfcEntityNames"]),
                ["synonyms"] = Strings(c?["synonyms"]),
                ["parent"] = c?["parentClassReference"] is JObject parent
                    ? new JObject { ["uri"] = S(parent["uri"]), ["code"] = S(parent["code"]), ["name"] = S(parent["name"]) }
                    : null
            };
            var properties = Items(c?["classProperties"]).Select(ShapeClassProperty).ToList();
            result["properties"] = new JArray(properties.Select(p => p["property"]));
            result["loin_property_suggestions"] = new JArray(properties.Select(p => p["suggestion"]));
            result["relations"] = new JArray(Items(c?["classRelations"]).Select(r => new JObject
            {
                ["relation_type"] = S(r["relationType"]),
                ["related_class_uri"] = S(r["relatedClassUri"]),
                ["related_class_name"] = S(r["relatedClassName"]),
                ["fraction"] = r["fraction"]
            }));
            result["child_classes"] = new JArray(Items(c?["childClassReferences"]).Select(r => new JObject
            {
                ["uri"] = S(r["uri"]),
                ["code"] = S(r["code"]),
                ["name"] = S(r["name"])
            }));
            result["suggestions_mean"] =
                "Each suggestion is a project-context loin_property built only from what bSDD published. data_type is " +
                "never filled from bSDD's dataType (String/Real/Integer/Boolean are not IFC defined types); pick the IFC " +
                "type before running ids_from_loin. A suggestion without property_set is incomplete: bSDD did not name one.";
        }

        private static JObject ShapeClassProperty(JToken p)
        {
            var property = new JObject
            {
                ["name"] = S(p["name"]),
                ["code"] = S(p["propertyCode"]),
                ["uri"] = S(p["propertyUri"]),
                ["property_set"] = S(p["propertySet"]),
                ["dictionary_uri"] = S(p["propertyDictionaryUri"]),
                ["data_type"] = S(p["dataType"]),
                ["units"] = Strings(p["units"]),
                ["allowed_values"] = AllowedValues(p["allowedValues"]),
                ["pattern"] = S(p["pattern"]),
                ["min_inclusive"] = p["minInclusive"],
                ["max_inclusive"] = p["maxInclusive"],
                ["min_exclusive"] = p["minExclusive"],
                ["max_exclusive"] = p["maxExclusive"],
                ["is_required"] = p["isRequired"],
                ["predefined_value"] = S(p["predefinedValue"]),
                ["description"] = S(p["description"]) ?? S(p["definition"])
            };
            return new JObject { ["property"] = property, ["suggestion"] = Suggest(property) };
        }

        private static void ShapeProperty(JToken p, JObject result)
        {
            var property = new JObject
            {
                ["uri"] = S(p?["uri"]),
                ["code"] = S(p?["code"]),
                ["name"] = S(p?["name"]),
                ["dictionary_uri"] = S(p?["dictionaryUri"]),
                ["dictionary_name"] = S(p?["dictionaryName"]),
                ["status"] = S(p?["status"]),
                ["property_set"] = S(p?["propertySet"]),
                ["data_type"] = S(p?["dataType"]),
                ["units"] = Strings(p?["units"]),
                ["allowed_values"] = AllowedValues(p?["allowedValues"]),
                ["pattern"] = S(p?["pattern"]),
                ["min_inclusive"] = p?["minInclusive"],
                ["max_inclusive"] = p?["maxInclusive"],
                ["min_exclusive"] = p?["minExclusive"],
                ["max_exclusive"] = p?["maxExclusive"],
                ["related_ifc_entities"] = Strings(p?["relatedIfcEntityNames"]),
                ["definition"] = S(p?["definition"]),
                ["description"] = S(p?["description"])
            };
            result["property"] = property;
            result["loin_property_suggestion"] = Suggest(property);
        }

        private static void ShapeDictionaries(JToken body, JObject result)
        {
            result["total_count"] = body?["totalCount"];
            result["dictionaries"] = new JArray(Items(body?["dictionaries"]).Select(d => new JObject
            {
                ["uri"] = S(d["dictionaryUri"]) ?? S(d["uri"]),
                ["name"] = S(d["dictionaryName"]) ?? S(d["name"]),
                ["version"] = S(d["version"]),
                ["status"] = S(d["status"]),
                ["release_date"] = S(d["releaseDate"])
            }));
        }

        /// <summary>A loin_property built only from published fields; what is missing is named.</summary>
        private static JObject Suggest(JObject p)
        {
            var loin = new JObject();
            var missing = new JArray();
            if (S(p["property_set"]) != null) loin["property_set"] = S(p["property_set"]); else missing.Add("property_set");
            string name = S(p["code"]) ?? S(p["name"]);
            if (name != null) loin["name"] = name; else missing.Add("name");
            missing.Add("data_type");
            if (p["allowed_values"] is JArray av && av.Count > 0)
                loin["allowed_values"] = new JArray(av.Select(v => S(v["code"]) ?? S(v["value"])).Where(v => v != null));
            if (S(p["pattern"]) != null) loin["pattern"] = S(p["pattern"]);
            foreach (string b in new[] { "min_inclusive", "max_inclusive", "min_exclusive", "max_exclusive" })
                if (p[b] is JValue bv && (bv.Type == JTokenType.Integer || bv.Type == JTokenType.Float)) loin[b] = bv.DeepClone();
            if (p["units"] is JArray units && units.Count == 1) loin["unit"] = units[0].DeepClone();
            if (S(p["uri"]) != null) loin["uri"] = S(p["uri"]);
            if (S(p["description"]) != null) loin["description"] = S(p["description"]);
            return new JObject
            {
                ["loin_property"] = loin,
                ["complete"] = loin["property_set"] != null && loin["name"] != null,
                ["still_needed"] = missing,
                ["bsdd_data_type"] = S(p["data_type"])
            };
        }

        private static JArray AllowedValues(JToken t) => new JArray(Items(t).Take(500).Select(v => new JObject
        {
            ["code"] = S(v["code"]),
            ["value"] = S(v["value"]),
            ["uri"] = S(v["uri"]),
            ["description"] = S(v["description"])
        }));

        // ============================================================================
        // helpers
        // ============================================================================

        private static IEnumerable<JToken> Items(JToken t) => t is JArray a ? a : Enumerable.Empty<JToken>();

        private static JArray Strings(JToken t) => new JArray(Items(t).Select(x => S(x)).Where(x => x != null).Take(200));

        /// <summary>A string field, bounded: bSDD text is published by third parties and goes to a model.</summary>
        private static string S(JToken t)
        {
            if (!(t is JValue v) || v.Type == JTokenType.Null) return null;
            string s = v.Type == JTokenType.String ? (string)v : v.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s.Length > MaxText ? s.Substring(0, MaxText) + "..." : s;
        }

        private static string Str(JToken t)
            => t is JValue v && v.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)v) ? ((string)v).Trim() : null;

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);

        private static string RequireText(JObject args, int min)
        {
            string text = Str(args["text"]);
            if (text == null) throw new ToolRefusal("text is required for this operation. Nothing was fetched.");
            return CheckText(text, min);
        }

        private static string CheckText(string text, int min)
        {
            if (text.Length < min) throw new ToolRefusal("text needs at least " + min + " characters. Nothing was fetched.");
            if (text.Length > 200) throw new ToolRefusal("text is limited to 200 characters. Nothing was fetched.");
            return text;
        }

        private static string RequireUri(JObject args, string key)
        {
            string uri = Str(args[key]);
            if (uri == null) throw new ToolRefusal(key + " is required for this operation. Nothing was fetched.");
            return CheckUri(uri, key);
        }

        private static string CheckUri(string uri, string key)
        {
            if (uri.Length > 1000 || !Uri.TryCreate(uri, UriKind.Absolute, out Uri u) ||
                (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                throw new ToolRefusal(key + " must be an absolute bSDD URI such as " +
                                      "https://identifier.buildingsmart.org/uri/...; '" + uri + "' is not. Nothing was fetched.");
            return uri;
        }

        private static IEnumerable<string> UriList(JObject args, string key)
        {
            if (args[key] == null || args[key].Type == JTokenType.Null) yield break;
            if (!(args[key] is JArray a)) throw new ToolRefusal(key + " must be an array of URIs. Nothing was fetched.");
            if (a.Count > 20) throw new ToolRefusal(key + " takes at most 20 dictionaries. Nothing was fetched.");
            foreach (JToken t in a)
            {
                string s = Str(t);
                if (s == null) throw new ToolRefusal(key + " holds an empty entry. Nothing was fetched.");
                yield return CheckUri(s, key);
            }
        }

        private static int Bounded(JObject args, string key, int dflt, int min, int max)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            if (t.Type != JTokenType.Integer) throw new ToolRefusal(key + " must be an integer. Nothing was fetched.");
            long v = (long)t;
            if (v < min || v > max) throw new ToolRefusal(key + " must be between " + min + " and " + max + ". Nothing was fetched.");
            return (int)v;
        }
    }
}
