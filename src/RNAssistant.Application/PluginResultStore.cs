using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
namespace RNAssistant.Application
{
    // Disposable result staging, not a data project or a durable read model. The
    // existing file/CAS authority pins completed bytes; only a live owner can read.
    public sealed class PluginResultStore : IDisposable
    {
        private sealed class Result { public string Owner; public string Release; public string Name; public WorkspaceDescriptor Workspace; public WorkspaceFileSnapshot File; }
        private readonly AppDataPaths _paths;
        private readonly WorkspaceFileService _files;
        private readonly Dictionary<string,Result> _results=new Dictionary<string,Result>();
        private readonly object _sync=new object();
        private bool _closed;
        public PluginResultStore(AppDataPaths paths){_paths=paths;_files=new WorkspaceFileService(paths);}
        public PluginFrameResult Publish(PluginInstance instance,string name,string staged,CancellationToken token)
        {
            lock(_sync)
            {
                if(_closed)throw new ObjectDisposedException(nameof(PluginResultStore));
                if (!PluginPackageFiles.ValidPath(name) || name.Length > 240 || _results.Values.Any(value => value.Owner == instance.ResultOwnerId && value.Name == name))
                    throw new PluginException("plugin_result_name", "Choose an unused relative result name; discard an old result before replacing it.");
                var length=new FileInfo(staged).Length;
                if(_results.Count>=128 || _results.Values.Where(value=>value.Owner==instance.ResultOwnerId).Sum(value=>value.File.ByteLength)+length>1024L*1024*1024)
                    throw new PluginException("plugin_result_limit","Discard old result sessions before producing more results.");
                var workspace=new WorkspaceStore(_paths).Open(Path.Combine(_paths.Root,"plugin-results",instance.ResultOwnerId),true,true);
                var id=Guid.NewGuid().ToString("N");
                var saved=_files.CommitStagedFile(workspace,id+".json",staged,null,PluginInstance.MaximumDataBytes,token:token);
                _results.Add(id,new Result{Owner=instance.ResultOwnerId,Release=instance.Release.ReleaseId,Name=name,Workspace=workspace,File=saved});
                return Describe(instance,name);
            }
        }
        public PluginFrameResult Describe(PluginInstance instance,string name)
        {
            lock(_sync)
            {
                var id = _results.FirstOrDefault(item => item.Value.Owner == instance.ResultOwnerId && item.Value.Name == name).Key;
                var result=Require(instance,id);
                return new PluginFrameResult{Handle=id,Name=result.Name,ByteLength=result.File.ByteLength,Url=instance.Origin+"/.result/read/"+id};
            }
        }
        public void Drop(PluginInstance instance, string name)
        {
            lock (_sync)
            {
                var id = _results.FirstOrDefault(item => item.Value.Owner == instance.ResultOwnerId && item.Value.Name == name).Key;
                Require(instance, id); Remove(id);
            }
        }
        public void ReleaseOwner(string owner)
        { lock (_sync) foreach (var id in _results.Where(item => item.Value.Owner == owner).Select(item => item.Key).ToArray()) Remove(id); }
        private void Remove(string id)
        {
            var result = _results[id];
            // Only our private scratch workspace is removed, never the selected data project.
            File.Delete(PluginPackageFiles.Resolve(result.Workspace.RootPath, result.File.RelativePath, false));
            try { _files.CaptureFile(result.Workspace, result.File.RelativePath, PluginInstance.MaximumDataBytes); }
            catch (FileNotFoundException) { /* Existing file authority records the missing scratch head. */ }
            _results.Remove(id);
        }
        public byte[] Read(PluginInstance instance,string id,long offset,int count,CancellationToken token)
        {
            lock(_sync)
            {
                var result=Require(instance,id);
                if(offset<0 || count<0 || count>PluginInstance.MaximumChunkBytes || offset>result.File.ByteLength-count)throw new PluginException("plugin_range_invalid","Invalid result range.");
                using(var input=_files.OpenExactRead(result.Workspace,result.File.RelativePath,result.File.Reference,PluginInstance.MaximumDataBytes,token))
                {input.Seek(offset,SeekOrigin.Begin);var bytes=new byte[count];var read=0;while(read<count){var n=input.Read(bytes,read,count-read);if(n==0)throw new EndOfStreamException();read+=n;}return bytes;}
            }
        }
        public Stream OpenDownload(PluginInstance instance,string id)
        {
            lock(_sync) { var result=Require(instance,id);return _files.OpenExactRead(result.Workspace,result.File.RelativePath,result.File.Reference,PluginInstance.MaximumDataBytes,instance.Token); }
        }
        private Result Require(PluginInstance instance,string id)
        {
            Result value;
            if(_closed || instance.Token.IsCancellationRequested || !_results.TryGetValue(id ?? "",out value) || value.Owner!=instance.ResultOwnerId || value.Release!=instance.Release.ReleaseId)
                throw new PluginException("plugin_result_denied","Result belongs to another invocation or publication.");
            return value;
        }
        public void Clear(){lock(_sync)foreach(var id in _results.Keys.ToArray())Remove(id);}
        public void Dispose(){lock(_sync){_closed=true;}}
    }
}
