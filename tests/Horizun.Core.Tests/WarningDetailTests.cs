using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests {
 public class WarningDetailTests {
  [Fact] public void GroupsByDescriptionAndSeverityAndPagesEveryElement() {
   var facts = new List<JObject> {
    JObject.Parse("{description:'same',severity:'Warning',elements:[{id:1,unique_id:'a'},{id:2,unique_id:'b'}]}"),
    JObject.Parse("{description:'same',severity:'Warning',elements:[{id:3,unique_id:'c'}]}"),
    JObject.Parse("{description:'same',severity:'Error',elements:[]}") };
   var groups = WarningDetailRules.Group(facts);
   Assert.Equal(2, groups.Count); Assert.Equal(2, (int)groups[1]["occurrences"]);
   var page = WarningDetailRules.Page(groups, 0, 1, 4096);
   Assert.True((bool)page["has_more"]); Assert.True((int)page["next_offset"] > 0);
   Assert.True(System.Text.Encoding.UTF8.GetByteCount(page.ToString(Newtonsoft.Json.Formatting.None)) <= 4096);
  }
  [Fact] public void ByteBudgetPagesLargeGroupsWithoutLosingElements() {
   var elements = new JArray(); for (int i = 0; i < 40; i++) elements.Add(new JObject { ["id"] = i, ["unique_id"] = new string('x', 80) + i });
   var facts = new[] { new JObject { ["description"] = "walls overlap", ["severity"] = "Warning", ["warning_index"] = 0, ["elements"] = elements, ["ids_complete"] = true } };
   var groups = WarningDetailRules.Group(facts); var seen = new HashSet<int>(); int offset = 0;
   while (true) { var page = WarningDetailRules.Page(groups, offset, 100, 2048); Assert.True(System.Text.Encoding.UTF8.GetByteCount(page.ToString(Newtonsoft.Json.Formatting.None)) <= 2048);
    foreach (JObject row in (JArray)page["records"]) Assert.True(seen.Add((int)row["element"]["id"]));
    if (!(bool)page["has_more"]) break; offset = (int)page["next_offset"]; }
   Assert.Equal(40, seen.Count);
  }
  [Fact] public void UnreadableIdsRemainExplicitAndTooSmallBudgetRefuses() {
   var facts = new[] { new JObject { ["description"] = new string('x', 3000), ["severity"] = "Warning", ["elements"] = new JArray(), ["ids_complete"] = false, ["ids_error"] = "unreadable" } };
   var groups = WarningDetailRules.Group(facts); Assert.Throws<System.ArgumentException>(() => WarningDetailRules.Page(groups, 0, 10, 1024));
   var page = WarningDetailRules.Page(groups, 0, 10, 8192); Assert.False((bool)page["records"][0]["ids_complete"]); Assert.Equal("unreadable", (string)page["records"][0]["ids_error"]);
  }
  [Fact] public void KnownWarningsOfferOnlyRehearsalAndUnknownDoNotGuess() {
   var duplicate = WarningDetailRules.Suggestion("Highlighted elements are identical in the same place", "model");
   Assert.Equal("horizun_delete_verified", (string)duplicate["tool"]); Assert.True((bool)duplicate["arguments"]["dry_run"]);
   var room = WarningDetailRules.Suggestion("Room is not enclosed", "model"); Assert.Equal("horizun_create_elements", (string)room["tool"]); Assert.True((bool)room["arguments"]["dry_run"]); Assert.Equal("room_separator", (string)room["arguments"]["elements"][0]["kind"]);
   Assert.Null(WarningDetailRules.Suggestion("A different warning", "model"));
  }
 }
}
