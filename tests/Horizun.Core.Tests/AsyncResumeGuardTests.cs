using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class AsyncResumeGuardTests : IDisposable
    {
        readonly string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
        readonly string root = Path.Combine(Path.GetTempPath(), "hz-resume-" + Guid.NewGuid().ToString("N"));
        readonly JObject args = new JObject { ["target_document"]="fixture", ["dry_run"]=false, ["confirmation_token"]="old" };
        public AsyncResumeGuardTests() { Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable,root); }
        public void Dispose() { Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable,saved); if(Directory.Exists(root)) Directory.Delete(root,true); }
        Job New() => Job.Start("horizun_transform_elements", resumeContext:AsyncResumeGuard.Context("horizun_transform_elements",args,"doc"));
        void Validate(Job job,bool alive=false,JObject request=null,string document="doc",string key=null) =>
            AsyncResumeGuard.Validate(job.Id,key??"resume:"+job.Id,"horizun_transform_elements",request??args,document,_=>alive);

        [Fact] public void Dead_queued_job_can_resume_with_fresh_confirmation_but_same_request()
        {
            var job=New(); var fresh=(JObject)args.DeepClone(); fresh["confirmation_token"]="new";
            Validate(job,request:fresh);
            Assert.Throws<InvalidOperationException>(()=>Validate(job,alive:true));
            Assert.Throws<InvalidOperationException>(()=>Validate(job,document:"other"));
            Assert.Throws<InvalidOperationException>(()=>Validate(job,key:"different"));
            fresh["dry_run"]=true;
            Assert.Throws<InvalidOperationException>(()=>Validate(job,request:fresh));
        }
        [Fact] public void Never_started_terminal_job_is_resumable_even_with_live_owner()
        { var job=New(); job.Finish("not_started","shutdown"); Validate(job,alive:true); }
        [Fact] public void Running_and_finished_jobs_are_never_replayed()
        {
            var job=New(); AsyncResumeGuard.Begin(job);
            Assert.Throws<InvalidOperationException>(()=>Validate(job));
            job.Result("{}"); job.Finish("ok",null);
            Assert.Throws<InvalidOperationException>(()=>Validate(job));
        }
        [Fact] public void Legacy_and_truncated_records_cannot_be_assumed_safe()
        {
            var legacy=Job.Start("horizun_transform_elements");
            Assert.Throws<InvalidOperationException>(()=>Validate(legacy));
            var job=New(); File.AppendAllText(job.Path,"{partial");
            Assert.Throws<InvalidOperationException>(()=>Validate(job));
        }
        sealed class FailingRunningSink : IJobSink
        {
            public void EnsureDirectory(string directory) {}
            public void Append(string path,string line) { if(line.Contains("\"running\"")) throw new IOException("disk full"); }
        }
        [Fact] public void Missing_running_record_prevents_command_execution()
        {
            var job=Job.Start("test",new FailingRunningSink()); int writes=0;
            Assert.Throws<InvalidOperationException>(()=>{ AsyncResumeGuard.Begin(job); writes++; });
            Assert.Equal(0,writes); Assert.False(job.RecordIsComplete);
        }
    }
}
