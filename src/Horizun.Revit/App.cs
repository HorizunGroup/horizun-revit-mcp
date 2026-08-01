// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The Revit add-in entry point. On startup we build the dispatcher, register the
// commands, create the ExternalEvent (must happen here, on the UI thread), start
// the pipe transport, and publish the discovery file so the MCP server can find
// us. On shutdown we take the discovery file down and stop the pipe.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;

namespace Horizun.Revit
{
    public sealed class App : IExternalApplication
    {
        private PipeServer _pipe;
        private Dispatcher _dispatcher;
        private string _year;

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                _year = app.ControlledApplication.VersionNumber;

                // Before anything else writes: settings, discovery, jobs and logs all
                // live under one root, and creating it here means the FIRST line of the
                // log is already in the right place. Best effort - a process that cannot
                // create them still starts, and horizun_health is what says so.
                HorizunPaths.EnsureDirectories();

                Log.Start(_year);
                Log.Info("startup: Revit " + _year + ", add-in " + Build.Version +
                         ", data root " + Safe(() => HorizunPaths.DataRoot()));

                // BEFORE the bridge, and deliberately not gated on it. If the pipe fails
                // to start, the tab is the only thing that can say so to somebody who is
                // looking at Revit rather than at a log - which is everybody, the first
                // time. Its own failure is logged and swallowed: a ribbon is never a
                // reason for an add-in to not load.
                try { Ribbon.Build(app); }
                catch (Exception rex) { Log.Warn("the ribbon tab could not be built: " + rex.Message); }

                _dispatcher = new Dispatcher();
                RegisterCommands(_dispatcher);
                _dispatcher.Initialize();   // ExternalEvent.Create — UI thread, here.

                string token = Discovery.NewToken();
                _pipe = new PipeServer(_dispatcher, token);
                _pipe.Start();

                // Version and command list go in the file: the server is deployed
                // separately and has no other way to know what this add-in can do.
                Discovery.Write(_year, _pipe.PipeName, token, Build.Version, _dispatcher.CommandNames);
                Log.Info("started: pipe '" + _pipe.PipeName + "', " +
                         System.Linq.Enumerable.Count(_dispatcher.CommandNames) + " commands, discovery at " +
                         System.IO.Path.Combine(Discovery.Dir(), Discovery.FileName(_year)));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Never take Revit down on a plugin failure — but never let it be silent
                // either. Without this line the only symptom on someone else's machine is
                // that nothing happens, which looks exactly like "not installed".
                Log.Error("STARTUP FAILED - no discovery file was written, so no MCP client can connect.", ex);
                return Result.Succeeded;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            // ORDER MATTERS. Stop the pipe FIRST so nothing new can be queued while we
            // are closing the records of what is already there, then drain.
            try { _pipe?.Stop(); } catch { }

            int synchronous = 0;
            try { synchronous = _dispatcher?.Shutdown() ?? 0; } catch { }

            // AsyncQueue.DrainForShutdown() existed, returned the list, and NOTHING
            // CALLED IT. So every job still waiting when Revit closed kept an open
            // record - reported by job_status as the ambiguity it refuses to resolve,
            // "still running, or the process died", when the truth was known exactly:
            // it never started. Each one is now closed as not_started.
            int abandoned = 0;
            try { abandoned = AsyncPump.DrainForShutdown(Log.Warn); } catch { }

            try { Discovery.Delete(_year); } catch { }
            try
            {
                Log.Info("shutdown: pipe stopped, discovery removed" +
                         (abandoned + synchronous > 0
                             ? ", " + synchronous + " queued call(s) and " + abandoned +
                               " async job(s) closed as not_started - they never ran"
                             : ", nothing was queued"));
            }
            catch { }
            return Result.Succeeded;
        }

        /// <summary>A path we could not resolve must not stop Revit from starting.</summary>
        private static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception ex) { return "(unresolved: " + ex.Message + ")"; }
        }

        private static void RegisterCommands(Dispatcher d)
        {
            d.Register(new GetDocumentInfoCommand());
            d.Register(new ExecutePythonCommand());
            d.Register(new ModelScanCommand());
            d.Register(new WriteParamsCommand());
            d.Register(new DeleteCommand());
            d.Register(new DocumentSessionCommand());
            d.Register(new AuditModelCommand());
            d.Register(new QuantitiesCommand());
            d.Register(new ClashCommand());
            d.Register(new SetKeynoteCommand());
            d.Register(new FamilyApplyCommand());
            d.Register(new BindSharedParamCommand());
            d.Register(new HealthCommand());
            d.Register(new SaveDocumentCommand());
            d.Register(new OpenDocumentCommand());
            d.Register(new RelinquishAllCommand());
            d.Register(new CaptureViewCommand());
            d.Register(new CreateScheduleCommand());
            d.Register(new ListElementsCommand());
            d.Register(new ListSchedulesCommand());
            d.Register(new GetScheduleDataCommand());

            // Recipe-backed tools: the Horizun AEC pyRevit buttons, as commands. The
            // geometry lives in Recipes\*.py; the transaction, the dry run and the
            // after-the-commit verification live in RecipeCommand. See Recipe.cs.
            d.Register(new SplitFloorLoopsCommand());
            d.Register(new SplitMultilayerWallsCommand());
            d.Register(new SplitMultilayerSlabsCommand());
            d.Register(new UngroupAndMarkCommand());
            d.Register(new RegroupByParamCommand());
            d.Register(new CopySlabElevationsCommand());
            d.Register(new EmbedFloorsInToposolidCommand());
            d.Register(new GradeToposolidCommand());
            d.Register(new RectangularizeWallsCommand());
            // more commands land here as they are ported.
        }
    }
}
