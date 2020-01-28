﻿using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Jewelry.Collections;
using JitPad.Core.Processor;
using JitPad.Core.Processor.Interface;
using JitPad.Foundation;
using Reactive.Bindings.Extensions;

namespace JitPad.Core
{
    public class BuildingUnit : NotificationObject, IDisposable
    {
        #region SourceCode

        private string _SourceCode = "";

        public string SourceCode
        {
            get => _SourceCode;
            set
            {
                if (SetProperty(ref _SourceCode, value))
                    BuildDetailMessages = Array.Empty<CompileResult.Message>();
            }
        }

        #endregion

        #region BuildResult

        private string _BuildResult = "";

        public string BuildResult
        {
            get => _BuildResult;
            private set => SetProperty(ref _BuildResult, value);
        }

        #endregion

        #region BuildMessage

        private string _BuildMessage = "";

        public string BuildMessage
        {
            get => _BuildMessage;
            private set => SetProperty(ref _BuildMessage, value);
        }

        #endregion

        #region BuildDetailMessages

        private CompileResult.Message[] _BuildDetailMessages = Array.Empty<CompileResult.Message>();

        public CompileResult.Message[] BuildDetailMessages
        {
            get => _BuildDetailMessages;
            private set => SetProperty(ref _BuildDetailMessages, value);
        }

        #endregion

        #region IsBuildOk

        private bool _IsBuildOk;

        public bool IsBuildOk
        {
            get => _IsBuildOk;
            private set => SetProperty(ref _IsBuildOk, value);
        }

        #endregion

        #region IsInBuilding

        private bool _IsInBuilding;

        public bool IsInBuilding
        {
            get => _IsInBuilding;
            private set => SetProperty(ref _IsInBuilding, value);
        }

        #endregion

        private readonly ICompiler _compiler;
        private readonly IDisassembler _disassembler;

        private class BuildContext : IEquatable<BuildContext>
        {
            public bool Equals(BuildContext? other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return SourceCode == other.SourceCode && IsReleaseBuild == other.IsReleaseBuild && IsTieredJit == other.IsTieredJit;
            }

            public override bool Equals(object? obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != GetType()) return false;
                return Equals((BuildContext) obj);
            }

            public override int GetHashCode()
                => HashCode.Combine(SourceCode, IsReleaseBuild, IsTieredJit);

            public readonly string SourceCode;
            public readonly bool IsReleaseBuild;
            public readonly bool IsTieredJit;

            public BuildContext(string sourceCode, bool isReleaseBuild, bool isTieredJit)
            {
                SourceCode = sourceCode;
                IsReleaseBuild = isReleaseBuild;
                IsTieredJit = isTieredJit;
            }
        }

        private class BuildResultData
        {
            public readonly bool IsOk;
            public readonly string Result;
            public readonly string Message;
            public readonly CompileResult.Message[] DetailMessages;

            public BuildResultData(bool isOk, string result, string message, CompileResult.Message[] detailMessages)
            {
                IsOk = isOk;
                Result = result;
                Message = message;
                DetailMessages = detailMessages;
            }
        }

        private readonly LruCache<BuildContext, BuildResultData> _buildCaches
            = new LruCache<BuildContext, BuildResultData>(16, true);

        private readonly CompositeDisposable _Trashes = new CompositeDisposable();

        public BuildingUnit(Config config, ICompiler compiler, IDisassembler disassembler)
        {
            _compiler = compiler;
            _disassembler = disassembler;

            this.ObserveProperty(x => x.SourceCode)
                .Throttle(TimeSpan.FromMilliseconds(500))
                .ObserveOn(ThreadPoolScheduler.Instance)
                .Subscribe(x =>
                {
                    var buildContext = new BuildContext(x, config.IsReleaseBuild, config.IsTieredJit);

                    if (_buildCaches.Contains(buildContext) == false)
                        Build(buildContext);
                })
                .AddTo(_Trashes);

            Observable
                .Merge(config.ObserveProperty(x => x.IsReleaseBuild))
                .Merge(config.ObserveProperty(x => x.IsTieredJit))
                .ObserveOn(ThreadPoolScheduler.Instance)
                .Subscribe(x =>
                {
                    var buildContext = new BuildContext(SourceCode, config.IsReleaseBuild, config.IsTieredJit);

                    if (_buildCaches.Contains(buildContext) == false)
                        Build(buildContext);
                })
                .AddTo(_Trashes);

            Observable
                .Merge(this.ObserveProperty(x => x.SourceCode).ToUnit())
                .Merge(config.ObserveProperty(x => x.IsReleaseBuild).ToUnit())
                .Merge(config.ObserveProperty(x => x.IsTieredJit).ToUnit())
                .Subscribe(x =>
                {
                    var buildContext = new BuildContext(SourceCode, config.IsReleaseBuild, config.IsTieredJit);

                    if (_buildCaches.Contains(buildContext))
                        LoadFromCache(buildContext);
                })
                .AddTo(_Trashes);
        }

        private void Build(BuildContext buildContext)
        {
            try
            {
                IsInBuilding = true;

                BuildDetailMessages = Array.Empty<CompileResult.Message>();

                var buildResultData = BuildCore(buildContext);

                // Add to cache
                _buildCaches.Add(buildContext, buildResultData);

                BuildDetailMessages = buildResultData.DetailMessages;
                IsBuildOk = buildResultData.IsOk;

                if (buildResultData.IsOk)
                    BuildResult = buildResultData.Result;
                else
                    BuildMessage = buildResultData.Message;
            }
            finally
            {
                IsInBuilding = false;
            }
        }

        private BuildResultData BuildCore(BuildContext buildContext)
        {
            if (string.IsNullOrEmpty(buildContext.SourceCode.Trim()))
                return new BuildResultData(true, "", "", Array.Empty<CompileResult.Message>());

            // compile
            var compileResult = _compiler.Run(buildContext.SourceCode, buildContext.IsReleaseBuild);
            if (compileResult.IsOk == false)
                return new BuildResultData(
                    false,
                    "",
                    string.Join("\n", compileResult.Messages.Select(x => x.ToString())),
                    compileResult.Messages);

            // jit disassemble
            var result = _disassembler.Run(buildContext.SourceCode, compileResult.AssembleImage, buildContext.IsTieredJit);

            return new BuildResultData(
                result.IsOk,
                result.Output,
                string.Join("\n", result.Messages),
                Array.Empty<CompileResult.Message>());
        }

        private void LoadFromCache(BuildContext buildContext)
        {
            var buildResultData = _buildCaches.Get(buildContext);

            BuildDetailMessages = buildResultData.DetailMessages;
            IsBuildOk = buildResultData.IsOk;

            if (buildResultData.IsOk)
                BuildResult = buildResultData.Result;
            else
                BuildMessage = buildResultData.Message;
        }

        public void Dispose()
        {
            _Trashes.Dispose();
        }
    }
}