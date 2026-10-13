// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Backlog #35: the promotion registry was guarded by a `lock` - one server's lock.
// Claude's and Codex's servers each load, change and save the same manifests, so a
// review in one could be overwritten by an approval in the other. The registry now
// takes a named mutex on its root; a second holder (another thread, its own Mutex
// instance: what another process looks like) makes every operation refuse, naming
// the path, rather than read or write behind it.
// -----------------------------------------------------------------------------
using System;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public sealed class ScriptPromotionLockTests : IDisposable
    {
        private readonly ExcelBackupRoot _root = new ExcelBackupRoot();
        private readonly TimeSpan _savedWait = ScriptPromotionTool.MutexWait;

        public void Dispose()
        {
            ScriptPromotionTool.MutexWait = _savedWait;
            _root.Dispose();
        }

        [Fact]
        public void The_registry_held_by_another_process_is_refused_naming_its_path()
        {
            ScriptPromotionTool.MutexWait = TimeSpan.FromMilliseconds(200);
            string root = ScriptPromotion.Root();

            using (new OtherHolder(HostFileMutex.NameFor(ScriptPromotionTool.MutexScope, root)))
            {
                ToolRefusal refused = Assert.Throws<ToolRefusal>(() =>
                    ScriptPromotionTool.Handle(new JObject { ["operation"] = "list" }, CancellationToken.None));
                Assert.Contains(root, refused.Message);
            }

            // Released, the same call is answered.
            JObject listed = ScriptPromotionTool.Handle(new JObject { ["operation"] = "list" }, CancellationToken.None);
            Assert.NotNull(listed);
        }
    }
}
