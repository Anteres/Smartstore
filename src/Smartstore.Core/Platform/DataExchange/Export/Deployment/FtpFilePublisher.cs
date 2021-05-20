﻿using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Dasync.Collections;
using Microsoft.Extensions.Logging;
using Smartstore.IO;

namespace Smartstore.Core.DataExchange.Export.Deployment
{
    public class FtpFilePublisher : IFilePublisher
    {
        private ExportDeployment _deployment;
        private ExportDeploymentContext _context;
        private CancellationToken _cancellationToken;
        private int _succeededFiles;
        private string _ftpRootUrl;

        public async Task PublishAsync(ExportDeployment deployment, ExportDeploymentContext context, CancellationToken cancellationToken)
        {
            _deployment = deployment;
            _context = context;
            _cancellationToken = cancellationToken;
            _succeededFiles = 0;
            _ftpRootUrl = deployment.Url;

            if (!_ftpRootUrl.StartsWith("ftp://", StringComparison.InvariantCultureIgnoreCase))
            {
                _ftpRootUrl = "ftp://" + _ftpRootUrl;
            }

            _ftpRootUrl = _ftpRootUrl.EnsureEndsWith("/");

            if (context.CreateZipArchive)
            {
                if (context.ZipFile?.Exists ?? false)
                {
                    await UploadFile(context.ZipFile, _ftpRootUrl + context.ZipFile.Name, false);
                }
            }
            else
            {
                await FtpCopyDirectory(context.ExportDirectory);
            }

            context.Log.Info($"{_succeededFiles} file(s) successfully uploaded via FTP.");
        }

        private async Task FtpCopyDirectory(IDirectory directory)
        {
            if (directory.SubPath.IsEmpty())
            {
                return;
            }

            var files = await directory.FileSystem
                .EnumerateFilesAsync(directory.SubPath)
                .ToListAsync(_cancellationToken);
            var lastFile = files.Last();

            foreach (var file in files)
            {
                var relativePath = GetRelativePath(file.PhysicalPath); // TODO: (mg) (core) Refactor, don't just copy. We now have "file.SubPath", which IS relative already.

                await UploadFile(file, _ftpRootUrl + relativePath, file != lastFile);
            }

            var subdirs = await directory.FileSystem
                .EnumerateDirectoriesAsync(directory.SubPath)
                .ToListAsync(_cancellationToken);

            foreach (var subdir in subdirs)
            {
                var relativePath = GetRelativePath(subdir.PhysicalPath); // TODO: (mg) (core) see above
                var url = _ftpRootUrl + relativePath;

                if (!await IsExistingDirectory(url))
                {
                    // TODO: (mg) (core) (perf) Why create a fresh request for every single iteration. Why not scoped??!!!!
                    var request = CreateRequest(url, true);
                    request.Method = WebRequestMethods.Ftp.MakeDirectory;

                    using var response = (FtpWebResponse)await request.GetResponseAsync();
                    response.Close();
                }

                await FtpCopyDirectory(subdir);
            }
        }

        private async Task<bool> UploadFile(IFile file, string fileUrl, bool keepAlive = true)
        {
            var succeeded = false;
            var buffLength = 32768;
            var buff = new byte[buffLength];

            var request = CreateRequest(fileUrl, keepAlive, file.Length);
            request.Method = WebRequestMethods.Ftp.UploadFile;

            var requestStream = request.GetRequestStream();

            using (var stream = await file.OpenReadAsync())
            {
                while (true)
                {
                    // TODO: (mg) (core) (perf) Don't copy buffers, copy the stream! --> stream.CopyToAsync(requestStream)
                    var bytesRead = await stream.ReadAsync(buff.AsMemory(0, buffLength), _cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await requestStream.WriteAsync(buff.AsMemory(0, bytesRead), _cancellationToken);
                }
            }

            requestStream.Close();

            using (var response = (FtpWebResponse)await request.GetResponseAsync())
            {
                var statusCode = (int)response.StatusCode;
                succeeded = statusCode >= 200 && statusCode <= 299;

                if (succeeded)
                {
                    ++_succeededFiles;
                }
                else
                {
                    _context.Result.LastError = _context.T("Admin.Common.FtpStatus", statusCode, response.StatusCode.ToString());
                    _context.Log.Error("The FTP transfer failed. FTP status {0} ({1}). File {3}".FormatInvariant(statusCode, response.StatusCode.ToString(), file.PhysicalPath));
                }
            }

            return succeeded;
        }

        private async Task<bool> IsExistingDirectory(string directoryUrl)
        {
            var result = false;

            try
            {
                var request = CreateRequest(directoryUrl.EnsureEndsWith("/"));
                request.Method = WebRequestMethods.Ftp.ListDirectory;

                using var _ = await request.GetResponseAsync();

                result = true;
            }
            catch (WebException)
            {
                result = false;
            }

            return result;
        }

        private FtpWebRequest CreateRequest(string url, bool keepAlive = true, long? contentLength = null)
        {
            var request = (FtpWebRequest)WebRequest.Create(url);
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.KeepAlive = keepAlive;
            request.UseBinary = true;
            request.Proxy = null;
            request.UsePassive = _deployment.PassiveMode;
            request.EnableSsl = _deployment.UseSsl;

            if (_deployment.Username.HasValue())
            {
                request.Credentials = new NetworkCredential(_deployment.Username, _deployment.Password);
            }

            if (contentLength.HasValue)
            {
                request.ContentLength = contentLength.Value;
            }

            return request;
        }

        private string GetRelativePath(string path)
        {
            var sourcePathLength = _context.ExportDirectory.PhysicalPath.Length;
            var relativePath = path[sourcePathLength..].Replace("\\", "/");

            if (relativePath.StartsWith("/"))
            {
                return relativePath[1..];
            }

            return relativePath;
        }
    }
}
