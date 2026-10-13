using System;
using System.IO;

namespace YooAsset
{
    internal sealed class LoadBuildinCatalogFileOperation : AsyncOperationBase
    {
        private enum ESteps
        {
            None,
            TryLoadFileData,
            RequestFileData,
            LoadCatalog,
            Done,
        }

        private readonly DefaultBuildinFileSystem _fileSystem;
        private UnityWebDataRequestOperation _webDataRequestOp;
        private byte[] _fileData;
        private ESteps _steps = ESteps.None;
        private float _fileStart;
        private float _wwwStart;
        private float _parseStart;

        /// <summary>
        /// 内置资源目录
        /// </summary>
        public DefaultBuildinFileCatalog Catalog;

        internal LoadBuildinCatalogFileOperation(DefaultBuildinFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }
        internal override void InternalStart()
        {
            _steps = ESteps.TryLoadFileData;
        }
        internal override void InternalUpdate()
        {
            if (_steps == ESteps.None || _steps == ESteps.Done)
                return;

            if (_steps == ESteps.TryLoadFileData)
            {
                string filePath = _fileSystem.GetCatalogBinaryFileLoadPath();
                _fileStart = BootPerf.Now;
                if (File.Exists(filePath))
                {
                    _fileData = File.ReadAllBytes(filePath);
                    BootPerf.End(
                        "yoo_catalog_file",
                        _fileStart,
                        $"exists=1 bytes={_fileData.Length} path={filePath}");
                    _steps = ESteps.LoadCatalog;
                }
                else if (AndroidStreamingAsset.TryReadBytes(filePath, out _fileData))
                {
                    BootPerf.End(
                        "yoo_catalog_file",
                        _fileStart,
                        $"exists=0 asset={_fileData.Length} path={filePath}");
                    _steps = ESteps.LoadCatalog;
                }
                else
                {
                    BootPerf.Mark("yoo_catalog_file", $"exists=0 path={filePath}");
                    _steps = ESteps.RequestFileData;
                }
            }

            if (_steps == ESteps.RequestFileData)
            {
                if (_webDataRequestOp == null)
                {
                    string filePath = _fileSystem.GetCatalogBinaryFileLoadPath();
                    string url = DownloadSystemHelper.ConvertToWWWPath(filePath);
                    _wwwStart = BootPerf.Now;
                    BootPerf.Begin("yoo_catalog_www");
                    _webDataRequestOp = new UnityWebDataRequestOperation(url, 60);
                    _webDataRequestOp.StartOperation();
                    AddChildOperation(_webDataRequestOp);
                }

                _webDataRequestOp.UpdateOperation();
                if (_webDataRequestOp.IsDone == false)
                    return;

                if (_webDataRequestOp.Status == EOperationStatus.Succeed)
                {
                    _fileData = _webDataRequestOp.Result;
                    BootPerf.End(
                        "yoo_catalog_www",
                        _wwwStart,
                        $"bytes={(_fileData != null ? _fileData.Length : 0)}");
                    _steps = ESteps.LoadCatalog;
                }
                else
                {
                    BootPerf.End("yoo_catalog_www", _wwwStart, $"err={_webDataRequestOp.Error}");
                    _steps = ESteps.Done;
                    Status = EOperationStatus.Failed;
                    Error = _webDataRequestOp.Error;
                }
            }

            if (_steps == ESteps.LoadCatalog)
            {
                try
                {
                    _parseStart = BootPerf.Now;
                    Catalog = CatalogTools.DeserializeFromBinary(_fileData);
                    int n = Catalog != null && Catalog.Wrappers != null ? Catalog.Wrappers.Count : 0;
                    BootPerf.End(
                        "yoo_catalog_parse",
                        _parseStart,
                        $"bytes={(_fileData != null ? _fileData.Length : 0)} wrappers={n}");
                    _steps = ESteps.Done;
                    Status = EOperationStatus.Succeed;
                }
                catch (Exception e)
                {
                    BootPerf.End("yoo_catalog_parse", _parseStart, $"err={e.Message}");
                    // AssetManager 同步读在 API<33 曾返回脏缓冲；退回 UnityWebRequest 再试一次。
                    if (_webDataRequestOp == null)
                    {
                        YooLogger.Warning($"Buildin catalog parse failed ({e.Message}), fallback WWW");
                        _fileData = null;
                        _steps = ESteps.RequestFileData;
                        return;
                    }

                    _steps = ESteps.Done;
                    Status = EOperationStatus.Failed;
                    Error = $"Failed to load catalog file : {e.Message}";
                }
            }
        }
    }
}