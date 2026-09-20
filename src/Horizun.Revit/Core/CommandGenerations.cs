// THE RUNTIME SYMBOL, derived from the one constant this project actually sets.
//
// Horizun.Revit.csproj sets DefineConstants to REVIT$(RevitYear) and nothing else, so
// the SDK's implicit framework symbols are not something to bet on here. The year is,
// and the mapping is fixed: Revit 2024 and earlier host .NET Framework 4.8, 2025 and
// later host modern .NET. Declared once, at the top, so the rest of the file reads as
// one decision rather than five.
#if REVIT2022 || REVIT2023 || REVIT2024
#define HORIZUN_LEGACY_RUNTIME
#endif
// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// RELOADING COMPILED COMMANDS. What is actually possible, per Revit version.
//
// THIS FILE EXISTS BECAUSE AN EARLIER VERSION OF THIS REPOSITORY SAID IT WAS
// IMPOSSIBLE. The sentence was: "un ensamblado .NET cargado en Revit no se puede
// descargar — ni en net48 ni en el .NET que hospeda Revit 2025+, porque el add-in
// cae en el contexto por defecto". The first half is true. The second half is a
// non-sequitur: the ADD-IN falls in the default context, and nothing makes a
// second assembly fall there too. Consulting the documentation instead of
// reasoning from the first half gives a different answer, and the answer differs
// by Revit version.
//
// WHAT THE DOCUMENTATION SAYS (read 2026-09-15, not assumed):
//
//   .NET Framework 4.8 — Revit 2023 and 2024.
//   There is no AssemblyLoadContext and no unloadability. The classic route,
//   a second AppDomain, does not apply here: Revit API objects do not cross an
//   AppDomain boundary, so code that touches a Document cannot live in one.
//   WHAT IS POSSIBLE is loading a NEW assembly from BYTES - Assembly.Load(byte[])
//   rather than LoadFrom(path), which takes no file lock so the file can be
//   replaced - and pointing the dispatch table at the new types. The previous
//   generation stays in memory for the life of the process. That is a LEAK, it is
//   bounded here, and it is reported rather than hidden. Autodesk's own Add-In
//   Manager does exactly this for the debug loop.
//
//   .NET 8 / .NET 10 — Revit 2025, 2026, 2027.
//   A collectible AssemblyLoadContext genuinely unloads. Unload() only INITIATES
//   it: the unload completes when no thread has a frame in those assemblies and
//   nothing outside holds a reference - including JIT-introduced stack slots,
//   statics, strong GC handles, pending RegisteredWaitHandles, and fields on the
//   load-context subclass itself. So this verifies completion through a
//   WeakReference and REPORTS a generation that would not go, rather than
//   claiming an unload it did not get.
//
// THE PUBLIC IMPLEMENTATION THAT DOES THIS, studied rather than guessed at:
// revit-mcp-v2 (mskim274) splits a stable host plugin from a reflection-discovered
// "CommandSet" assembly, hot-reloadable on Revit 2025+ through a collectible load
// context, with a staging script building an immutable generation and a tool call
// activating it - the new generation fully loaded and validated before the active
// command dictionary is swapped, and a failure leaving the previous generation
// running. Its own README states the limit this file also has: changes to the
// HOST plugin, to the contracts, or to the session lifecycle still need a restart.
//
// THE FOUR THINGS THAT MUST BE KEPT APART, and conflating them is what produced
// the original absolute:
//
//   1. THE MAIN ADD-IN Revit loads from its .addin manifest. Never reloadable, in
//      any version. Its IExternalApplication ran at startup, its ribbon exists,
//      and Revit holds references to both.
//   2. THE SHARED CONTRACT - ICommand, CommandResult, the JSON types. It MUST
//      stay in the default context and must NOT be duplicated into the
//      generation's context: two copies of one interface make every cast fail
//      with "unable to cast ICommand to ICommand", which is the single most
//      confusing error this design can produce.
//   3. THE GENERATION: the assembly holding the command implementations. This is
//      the only thing that moves.
//   4. THE REFERENCES THAT PREVENT AN UNLOAD. Cooperative means the host has to
//      be disciplined: no static caches of generation types, no delegates kept
//      past the swap, and the dispatch table rebuilt rather than patched.
//
// NOTHING HERE HAS BEEN COMPILED OR RUN.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
#if !HORIZUN_LEGACY_RUNTIME
using System.Runtime.Loader;
#endif

namespace Horizun.Revit.Core
{
    /// <summary>What this Revit's runtime can actually do with a loaded assembly.</summary>
    public enum ReloadCapability
    {
        /// <summary>A new generation can be loaded and dispatched to; the old one stays in memory.</summary>
        LoadNewWithoutUnload,

        /// <summary>A new generation can be loaded and the old one genuinely unloaded, cooperatively.</summary>
        LoadNewAndUnloadOld
    }

    public sealed class LoadedGeneration
    {
        public int Number;
        public string Sha256;
        public string Path;
        public string LoadedUtc;
        public Assembly Assembly;

        /// <summary>The command wire names this generation offers. Rebuilt per generation, never patched.</summary>
        public readonly List<string> CommandNames = new List<string>();

        /// <summary>Set when this generation has been retired and its unload asked for.</summary>
        public bool UnloadRequested;

        /// <summary>
        /// Null on .NET Framework. On modern .NET, a weak reference to the load context, which is
        /// how completion is OBSERVED rather than assumed.
        /// </summary>
        public WeakReference ContextWeakReference;

        public bool StillResident => ContextWeakReference != null && ContextWeakReference.IsAlive;
    }

    /// <summary>
    /// Generations of a reloadable command assembly, and an honest account of what each
    /// Revit version does with them.
    /// </summary>
    public static class CommandGenerations
    {
        /// <summary>
        /// On .NET Framework, every generation stays resident for the life of the process.
        /// The bound exists so a long session with a developer reloading all afternoon fails
        /// with a sentence instead of with an out-of-memory in Revit.
        /// </summary>
        public const int MaxResidentGenerationsWithoutUnload = 12;

        /// <summary>How many GC passes to wait for a cooperative unload before saying it did not happen.</summary>
        public const int UnloadObservationPasses = 10;

        public static ReloadCapability Capability =>
#if HORIZUN_LEGACY_RUNTIME
            ReloadCapability.LoadNewWithoutUnload;
#else
            ReloadCapability.LoadNewAndUnloadOld;
#endif

        /// <summary>The sentence the reply carries. Different per runtime, and never rounded up.</summary>
        public static string CapabilityStatement =>
#if HORIZUN_LEGACY_RUNTIME
            "This Revit hosts .NET Framework 4.8, which has no unloadable load context. A new " +
            "generation CAN be loaded and dispatched to - from bytes, so the file is never " +
            "locked - and the previous generation stays in memory for the life of the process. " +
            "That is a bounded leak, not an unload, and it is reported as one. A second AppDomain " +
            "is not an alternative: Revit API objects do not cross an AppDomain boundary.";
#else
            "This Revit hosts modern .NET, where a collectible AssemblyLoadContext genuinely " +
            "unloads. Unload is COOPERATIVE: it completes only when no thread has a frame in the " +
            "generation and nothing outside holds a reference to it. Completion is OBSERVED here " +
            "through a weak reference; a generation that would not go is reported as still " +
            "resident rather than counted as unloaded.";
#endif

        /// <summary>
        /// The limits that hold in EVERY version, said in the reply rather than discovered.
        ///
        /// These are not caveats around the mechanism; they are the mechanism's shape. A caller
        /// who edits the host and calls reload gets the old host and no error unless this is said.
        /// </summary>
        public static readonly string[] AlwaysRequiresRestart =
        {
            "the main add-in itself: Revit loaded it from its .addin manifest, ran its " +
            "IExternalApplication and holds its ribbon. Nothing can take that back.",

            "the shared contract (ICommand, CommandResult, the request and reply types). It lives " +
            "in the DEFAULT context on purpose: a second copy inside the generation would make " +
            "every cast fail with 'unable to cast ICommand to ICommand'.",

            "the transport and the session lifecycle: the pipe server, the request gate, the " +
            "dispatcher itself. Code with a frame on a live thread cannot be replaced underneath it.",

            "anything that has handed a delegate or an event handler to Revit. Revit is then " +
            "holding a reference into the generation, and on modern .NET that is exactly what " +
            "keeps the unload from completing."
        };

        // =====================================================================
        // Loading
        // =====================================================================

        /// <summary>
        /// Load a generation from disk, WITHOUT locking the file.
        ///
        /// Bytes rather than a path in both runtimes, and for the same reason in both: a path
        /// load takes a lock, and a locked generation cannot be replaced by the next stage step.
        /// The hash is computed over the bytes that were actually loaded, not over a second read
        /// of the file - between two reads somebody can replace it, and a generation whose hash
        /// describes different bytes than the ones running is worse than no hash at all.
        /// </summary>
        public static LoadedGeneration Load(string assemblyPath, int number, out string problem)
        {
            problem = null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(assemblyPath);
            }
            catch (Exception ex)
            {
                problem = "generation " + number + " could not be read from '" + assemblyPath + "': " +
                          ex.Message;
                return null;
            }

            string sha = Sha256(bytes);
            var generation = new LoadedGeneration
            {
                Number = number,
                Sha256 = sha,
                Path = assemblyPath,
                LoadedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            try
            {
#if HORIZUN_LEGACY_RUNTIME
                // NO UNLOAD IS POSSIBLE HERE, so there is nothing to keep clean: the assembly
                // goes into the default context and stays there.
                generation.Assembly = Assembly.Load(bytes);
#else
                var context = new GenerationLoadContext(number, assemblyPath);
                using (var stream = new MemoryStream(bytes))
                    generation.Assembly = context.LoadFromStream(stream);
                generation.ContextWeakReference = new WeakReference(context, trackResurrection: true);
#endif
            }
            catch (Exception ex)
            {
                problem = "generation " + number + " would not load: " + ex.Message +
                          ". Nothing was swapped; the previous generation is still the active one.";
                return null;
            }

            return generation;
        }

        /// <summary>
        /// The command types a generation offers, by reflection over the SHARED interface.
        ///
        /// The interface comes from the default context - `typeof(ICommand)` here resolves to the
        /// host's copy - which is what makes `IsAssignableFrom` mean anything across the boundary.
        /// A generation that shipped its own copy of the contract answers false to every check
        /// here and is refused with that named as the reason, rather than loading and then
        /// failing on the first cast.
        /// </summary>
        public static List<Type> DiscoverCommands(LoadedGeneration generation, out string problem)
        {
            problem = null;
            var found = new List<Type>();
            if (generation == null || generation.Assembly == null)
            {
                problem = "there is no loaded generation to reflect over.";
                return found;
            }

            Type[] types;
            try
            {
                types = generation.Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // SAY WHICH TYPE AND WHY. "Could not load one or more types" with no detail is
                // the least useful exception in .NET, and it is exactly the one a generation
                // built against the wrong contract version throws.
                string detail = string.Join("; ", (ex.LoaderExceptions ?? new Exception[0])
                    .Where(e => e != null).Select(e => e.Message).Take(5));
                problem = "generation " + generation.Number + " loaded and its types would not: " +
                          detail + ". The usual cause is a generation built against a different " +
                          "version of the shared contract than the one this add-in is running.";
                return found;
            }

            Type contract = typeof(ICommand);
            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;
                if (!contract.IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    problem = (problem ?? "") + "type " + type.FullName +
                              " implements ICommand and has no parameterless constructor; ";
                    continue;
                }
                found.Add(type);
            }

            if (found.Count == 0 && problem == null)
                problem = "generation " + generation.Number + " carries no type implementing the " +
                          "host's ICommand. If it ships its OWN copy of the contract assembly, " +
                          "the interfaces are two different types and nothing will ever match: " +
                          "the contract must be resolved from the default context.";
            return found;
        }

        // =====================================================================
        // Retiring
        // =====================================================================

        /// <summary>
        /// Ask a generation to go, and then OBSERVE whether it went.
        ///
        /// On .NET Framework the answer is always no and the reply says so. On modern .NET the
        /// answer is usually yes and sometimes no, and the difference matters: a caller who is
        /// told "unloaded" and whose memory keeps growing has been lied to about the one thing
        /// this mechanism exists to provide.
        /// </summary>
        public static string Retire(LoadedGeneration generation)
        {
            if (generation == null) return "there was no previous generation.";
            generation.UnloadRequested = true;

            // DROP THE HOST'S OWN REFERENCES FIRST. The assembly handle on this record is
            // itself a reference into the context, and leaving it set would be this code
            // preventing the unload it is about to measure.
            generation.Assembly = null;
            generation.CommandNames.Clear();

#if HORIZUN_LEGACY_RUNTIME
            return "generation " + generation.Number + " is RETIRED but still resident: .NET " +
                   "Framework 4.8 cannot unload an assembly. It will stay in memory until Revit " +
                   "closes. Nothing dispatches to it any more.";
#else
            WeakReference reference = generation.ContextWeakReference;
            if (reference == null)
                return "generation " + generation.Number + " has no load context recorded, so " +
                       "nothing can be unloaded or observed.";

            var context = reference.Target as GenerationLoadContext;
            if (context != null)
            {
                try { context.Unload(); }
                catch (Exception ex)
                {
                    return "generation " + generation.Number + " refused to start unloading: " +
                           ex.Message;
                }
            }
            context = null;

            // The loop the documentation prescribes: unload finishes on the GC's schedule, not
            // on this call's.
            for (int pass = 0; pass < UnloadObservationPasses && reference.IsAlive; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return reference.IsAlive
                ? "generation " + generation.Number + " was asked to unload and DID NOT: something " +
                  "still references it. The usual causes are a thread with a frame in its code, a " +
                  "delegate handed to Revit, a static cache, or a strong GC handle. It is retired " +
                  "from dispatch and still in memory, and this reply says so rather than reporting " +
                  "an unload that did not happen."
                : "generation " + generation.Number + " unloaded: the load context was collected.";
#endif
        }

        /// <summary>Why a new generation may not be loaded right now, or null.</summary>
        public static string AdmissionRefusal(IEnumerable<LoadedGeneration> resident)
        {
#if HORIZUN_LEGACY_RUNTIME
            int stillResident = resident == null ? 0 : resident.Count();
            if (stillResident >= MaxResidentGenerationsWithoutUnload)
                return "this Revit hosts .NET Framework 4.8, where a loaded assembly is never " +
                       "unloaded, and " + stillResident + " generation(s) are already resident - " +
                       "the bound is " + MaxResidentGenerationsWithoutUnload + ". Restart Revit " +
                       "to clear them. The bound exists so a long reload session fails with a " +
                       "sentence rather than with an out-of-memory inside somebody's model.";
            return null;
#else
            int stuck = resident == null ? 0 : resident.Count(g => g.UnloadRequested && g.StillResident);
            if (stuck >= MaxResidentGenerationsWithoutUnload)
                return stuck + " retired generation(s) were asked to unload and did not, which is " +
                       "the bound. Something in this session holds references into them; loading " +
                       "more would grow a leak this mechanism is supposed to avoid. Restart Revit, " +
                       "and look for a thread or a delegate held across a swap.";
            return null;
#endif
        }

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

#if !HORIZUN_LEGACY_RUNTIME
        /// <summary>
        /// The collectible context one generation lives in.
        ///
        /// `Load` returns null for everything, which sends every dependency - the shared
        /// contract above all - to the DEFAULT context. That is the decision the whole design
        /// turns on: resolving the contract here would give the generation its own copy of
        /// ICommand, and the host's cast would fail with a message that names the same type
        /// twice.
        ///
        /// It holds NO field referencing anything it loaded. The documentation is explicit that
        /// fields on the subclass keep the context alive while the unload is in progress, which
        /// is the one place where the instrument prevents its own measurement.
        /// </summary>
        private sealed class GenerationLoadContext : AssemblyLoadContext
        {
            private readonly string _describedPath;

            public GenerationLoadContext(int number, string path)
                : base("horizun-generation-" + number, isCollectible: true)
            {
                _describedPath = path;
            }

            protected override Assembly Load(AssemblyName name) => null;

            public override string ToString() => Name + " (" + _describedPath + ")";
        }
#endif
    }
}
