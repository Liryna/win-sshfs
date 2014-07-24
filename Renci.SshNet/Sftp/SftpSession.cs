﻿using System;
using System.Linq;
using System.Text;
using System.Threading;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using System.Collections.Generic;
using System.Globalization;
using Renci.SshNet.Sftp.Responses;
using Renci.SshNet.Sftp.Requests;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WinSshFS, PublicKey=0024000004800000940000000602000000240000525341310004000001000100bb1df492d3d63bb4aedb73672b0c0694ad7838ea17c6d49685ef1a03301ae6de5a4b4b1795844de0ddab49254d05bd533b5a02c24a580346274b204b8975536ffe36b4e7bb8c82e419264c335682ad44861e99eef16e95ee978742064c9335283ecb6e2fd325ea97942a51a3fc1a7d9948dd919b0d4ad25148d5490cc37f85b4")]

namespace Renci.SshNet.Sftp
{
    internal class SftpSession : SubsystemSession
    {
        private const int MAXIMUM_SUPPORTED_VERSION = 3;

        private const int MINIMUM_SUPPORTED_VERSION = 0;

        private readonly Dictionary<uint, SftpRequest> _requests = new Dictionary<uint, SftpRequest>();

        private readonly List<byte> _data = new List<byte>(16 * 1024);

        private EventWaitHandle _sftpVersionConfirmed = new AutoResetEvent(false);

        internal IDictionary<string, string> _supportedExtensions;

        /// <summary>
        /// Gets remote working directory.
        /// </summary>
        public string WorkingDirectory { get; private set; }

        /// <summary>
        /// Gets SFTP protocol version.
        /// </summary>
        public uint ProtocolVersion { get; private set; }

        private long _requestId;
        /// <summary>
        /// Gets the next request id for sftp session.
        /// </summary>
        public uint NextRequestId
        {
            get
            {
#if WINDOWS_PHONE
                lock (this)
                {
                    this._requestId++;
                }

                return (uint)this._requestId;
#else
                return ((uint)Interlocked.Increment(ref this._requestId));
#endif
            }
        }

        public SftpSession(Session session, TimeSpan operationTimeout, Encoding encoding)
            : base(session, "sftp", operationTimeout, encoding)
        {
        }

        public void ChangeDirectory(string path)
        {
            var fullPath = this.GetCanonicalPath(path);

            var handle = this.RequestOpenDir(fullPath);

            this.RequestClose(handle);

            this.WorkingDirectory = fullPath;
        }

        internal void SendMessage(SftpMessage sftpMessage)
        {
            var messageData = sftpMessage.GetBytes();

            var data = new byte[4 + messageData.Length];

            ((uint)messageData.Length).GetBytes().CopyTo(data, 0);
            messageData.CopyTo(data, 4);

            this.SendData(data);
        }

        /// <summary>
        /// Resolves path into absolute path on the server.
        /// </summary>
        /// <param name="path">Path to resolve.</param>
        /// <returns>Absolute path</returns>
        internal string GetCanonicalPath(string path)
        {
            var fullPath = GetFullRemotePath(path);

            var canonizedPath = string.Empty;

            var realPathFiles = this.RequestRealPath(fullPath, true);

            if (realPathFiles != null)
            {
                canonizedPath = realPathFiles.First().Key;
            }

            if (!string.IsNullOrEmpty(canonizedPath))
                return canonizedPath;

            //  Check for special cases
            if (fullPath.EndsWith("/.", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.EndsWith("/..", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.Equals("/", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.IndexOf('/') < 0)
                return fullPath;

            var pathParts = fullPath.Split(new char[] { '/' });

            var partialFullPath = string.Join("/", pathParts, 0, pathParts.Length - 1);

            if (string.IsNullOrEmpty(partialFullPath))
                partialFullPath = "/";

            realPathFiles = this.RequestRealPath(partialFullPath, true);

            if (realPathFiles != null)
            {
                canonizedPath = realPathFiles.First().Key;
            }

            if (string.IsNullOrEmpty(canonizedPath))
            {
                return fullPath;
            }

            var slash = string.Empty;
            if (canonizedPath[canonizedPath.Length - 1] != '/')
                slash = "/";
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}", canonizedPath, slash, pathParts[pathParts.Length - 1]);
        }

        internal string GetFullRemotePath(string path)
        {
            var fullPath = path;

            if (!string.IsNullOrEmpty(path) && path[0] != '/' && this.WorkingDirectory != null)
            {
                if (this.WorkingDirectory[this.WorkingDirectory.Length - 1] == '/')
                {
                    fullPath = string.Format(CultureInfo.InvariantCulture, "{0}{1}", this.WorkingDirectory, path);
                }
                else
                {
                    fullPath = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", this.WorkingDirectory, path);
                }
            }
            return fullPath;
        }

        protected override void OnChannelOpen()
        {
            this.SendMessage(new SftpInitRequest(MAXIMUM_SUPPORTED_VERSION));

            this.WaitOnHandle(this._sftpVersionConfirmed, this._operationTimeout);

            if (this.ProtocolVersion > MAXIMUM_SUPPORTED_VERSION || this.ProtocolVersion < MINIMUM_SUPPORTED_VERSION)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Server SFTP version {0} is not supported.", this.ProtocolVersion));
            }

            //  Resolve current directory
            this.WorkingDirectory = this.RequestRealPath(".").First().Key;
        }

        protected override void OnDataReceived(uint dataTypeCode, byte[] data)
        {
            //  Add channel data to internal data holder
            this._data.AddRange(data);

            while (this._data.Count > 4 + 1)
            {
                //  Extract packet length
                var packetLength = (this._data[0] << 24 | this._data[1] << 16 | this._data[2] << 8 | this._data[3]);

                //  Check if complete packet data is available
                if (this._data.Count < packetLength + 4)
                {
                    //  Wait for complete message to arrive first
                    break;
                }
                this._data.RemoveRange(0, 4);

                //  Create buffer to hold packet data
                var packetData = new byte[packetLength];

                //  Cope packet data to array
                this._data.CopyTo(0, packetData, 0, packetLength);

                //  Remove loaded data from _data holder
                this._data.RemoveRange(0, packetLength);

                //  Load SFTP Message and handle it
                var response = SftpMessage.Load(this.ProtocolVersion, packetData, this.Encoding);

                try
                {
                    var versionResponse = response as SftpVersionResponse;
                    if (versionResponse != null)
                    {
                        this.ProtocolVersion = versionResponse.Version;
                        this._supportedExtensions = versionResponse.Extentions;

                        this._sftpVersionConfirmed.Set();
                    }
                    else
                    {
                        this.HandleResponse(response as SftpResponse);
                    }
                }
                catch (Exception exp)
                {
                    this.RaiseError(exp);
                    break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (this._sftpVersionConfirmed != null)
                {
                    this._sftpVersionConfirmed.Dispose();
                    this._sftpVersionConfirmed = null;
                }
            }
        }

        private void SendRequest(SftpRequest request)
        {
            lock (this._requests)
            {
                this._requests.Add(request.RequestId, request);
            }

            this.SendMessage(request);
        }

        #region SFTP API functions

        /// <summary>
        /// Performs SSH_FXP_OPEN request
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="flags">The flags.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns>File handle.</returns>
        internal byte[] RequestOpen(string path, Flags flags, bool nullOnError = false)
        {
            byte[] handle = null;
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpOpenRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding, flags,
                    response =>
                    {
                        handle = response.Handle;
                        wait.Set();
                    },
                    response =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return handle;
        }

        /// <summary>
        /// Performs SSH_FXP_CLOSE request.
        /// </summary>
        /// <param name="handle">The handle.</param>
        internal void RequestClose(byte[] handle)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpCloseRequest(this.ProtocolVersion, this.NextRequestId, handle,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_READ request.
        /// </summary>
        /// <param name="handle">The handle.</param>
        /// <param name="offset">The offset.</param>
        /// <param name="length">The length.</param>
        /// <returns>data array; null if EOF</returns>
        internal byte[] RequestRead(byte[] handle, UInt64 offset, UInt32 length)
        {
            SshException exception = null;

            var data = new byte[0];

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpReadRequest(this.ProtocolVersion, this.NextRequestId, handle, offset, length,
                    (response) =>
                    {
                        data = response.Data;
                        wait.Set();
                    },
                    (response) =>
                    {
                        if (response.StatusCode != StatusCodes.Eof)
                        {
                            exception = this.GetSftpException(response);
                        }
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }

            return data;
        }

        /// <summary>
        /// Performs SSH_FXP_WRITE request.
        /// </summary>
        /// <param name="handle">The handle.</param>
        /// <param name="offset">The offset.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="wait">The wait event handle if needed.</param>
        /// <param name="writeCompleted">The callback to invoke when the write has completed.</param>
        internal void RequestWrite(byte[] handle, UInt64 offset, byte[] data, EventWaitHandle wait, Action<SftpStatusResponse> writeCompleted = null)
        {
            SshException exception = null;

            var request = new SftpWriteRequest(this.ProtocolVersion, this.NextRequestId, handle, offset, data,
                (response) =>
                {
                    if (writeCompleted != null)
                    {
                        writeCompleted(response);
                    }

                    exception = this.GetSftpException(response);
                    if (wait != null)
                        wait.Set();
                });

            this.SendRequest(request);

            if (wait != null)
                this.WaitOnHandle(wait, this._operationTimeout);

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_LSTAT request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns>
        /// File attributes
        /// </returns>
        internal SftpFileAttributes RequestLStat(string path, bool nullOnError = false)
        {
            SshException exception = null;

            SftpFileAttributes attributes = null;
            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpLStatRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        attributes = response.Attributes;
                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return attributes;
        }

        /// <summary>
        /// Performs SSH_FXP_FSTAT request.
        /// </summary>
        /// <param name="handle">The handle.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns>
        /// File attributes
        /// </returns>
        internal SftpFileAttributes RequestFStat(byte[] handle, bool nullOnError = false)
        {
            SshException exception = null;
            SftpFileAttributes attributes = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpFStatRequest(this.ProtocolVersion, this.NextRequestId, handle,
                    (response) =>
                    {
                        attributes = response.Attributes;
                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return attributes;
        }

        /// <summary>
        /// Performs SSH_FXP_SETSTAT request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="attributes">The attributes.</param>
        internal void RequestSetStat(string path, SftpFileAttributes attributes)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpSetStatRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding, attributes,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_FSETSTAT request.
        /// </summary>
        /// <param name="handle">The handle.</param>
        /// <param name="attributes">The attributes.</param>
        internal void RequestFSetStat(byte[] handle, SftpFileAttributes attributes)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpFSetStatRequest(this.ProtocolVersion, this.NextRequestId, handle, attributes,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_OPENDIR request
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns>File handle.</returns>
        internal byte[] RequestOpenDir(string path, bool nullOnError = false)
        {
            SshException exception = null;

            byte[] handle = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpOpenDirRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        handle = response.Handle;
                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return handle;
        }

        /// <summary>
        /// Performs SSH_FXP_READDIR request
        /// </summary>
        /// <param name="handle">The handle.</param>
        /// <returns></returns>
        internal KeyValuePair<string, SftpFileAttributes>[] RequestReadDir(byte[] handle)
        {
            SshException exception = null;

            KeyValuePair<string, SftpFileAttributes>[] result = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpReadDirRequest(this.ProtocolVersion, this.NextRequestId, handle,
                    (response) =>
                    {
                        result = response.Files;
                        wait.Set();
                    },
                    (response) =>
                    {
                        if (response.StatusCode != StatusCodes.Eof)
                        {
                            exception = this.GetSftpException(response);
                        }
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }

            return result;
        }

        /// <summary>
        /// Performs SSH_FXP_REMOVE request.
        /// </summary>
        /// <param name="path">The path.</param>
        internal void RequestRemove(string path)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpRemoveRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_MKDIR request.
        /// </summary>
        /// <param name="path">The path.</param>
        internal void RequestMkDir(string path)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpMkDirRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_RMDIR request.
        /// </summary>
        /// <param name="path">The path.</param>
        internal void RequestRmDir(string path)
        {
            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpRmDirRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_REALPATH request
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns></returns>
        internal KeyValuePair<string, SftpFileAttributes>[] RequestRealPath(string path, bool nullOnError = false)
        {
            SshException exception = null;

            KeyValuePair<string, SftpFileAttributes>[] result = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpRealPathRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        result = response.Files;
                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }
            
            return result;
        }

        /// <summary>
        /// Performs SSH_FXP_STAT request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns>
        /// File attributes
        /// </returns>
        internal SftpFileAttributes RequestStat(string path, bool nullOnError = false)
        {
            SshException exception = null;

            SftpFileAttributes attributes = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpStatRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        attributes = response.Attributes;
                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return attributes;
        }

        /// <summary>
        /// Performs SSH_FXP_RENAME request.
        /// </summary>
        /// <param name="oldPath">The old path.</param>
        /// <param name="newPath">The new path.</param>
        internal void RequestRename(string oldPath, string newPath)
        {
            if (this.ProtocolVersion < 2)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_RENAME operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpRenameRequest(this.ProtocolVersion, this.NextRequestId, oldPath, newPath, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs SSH_FXP_READLINK request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> returns null instead of throwing an exception.</param>
        /// <returns></returns>
        internal KeyValuePair<string, SftpFileAttributes>[] RequestReadLink(string path, bool nullOnError = false)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_READLINK operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            KeyValuePair<string, SftpFileAttributes>[] result = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpReadLinkRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        result = response.Files;

                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return result;
        }

        /// <summary>
        /// Performs SSH_FXP_SYMLINK request.
        /// </summary>
        /// <param name="linkpath">The linkpath.</param>
        /// <param name="targetpath">The targetpath.</param>
        internal void RequestSymLink(string linkpath, string targetpath)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_SYMLINK operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new SftpSymLinkRequest(this.ProtocolVersion, this.NextRequestId, linkpath, targetpath, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        #endregion

        #region SFTP Extended API functions

        /// <summary>
        /// Performs posix-rename@openssh.com extended request.
        /// </summary>
        /// <param name="oldPath">The old path.</param>
        /// <param name="newPath">The new path.</param>
        internal void RequestPosixRename(string oldPath, string newPath)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_EXTENDED operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new PosixRenameRequest(this.ProtocolVersion, this.NextRequestId, oldPath, newPath, this.Encoding,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                if (!this._supportedExtensions.ContainsKey(request.Name))
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Extension method {0} currently not supported by the server.", request.Name));

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        /// <summary>
        /// Performs statvfs@openssh.com extended request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="nullOnError">if set to <c>true</c> [null on error].</param>
        /// <returns></returns>
        internal SftpFileSytemInformation RequestStatVfs(string path, bool nullOnError = false)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_EXTENDED operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            SftpFileSytemInformation information = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new StatVfsRequest(this.ProtocolVersion, this.NextRequestId, path, this.Encoding,
                    (response) =>
                    {
                        information = response.GetReply<StatVfsReplyInfo>().Information;

                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                if (!this._supportedExtensions.ContainsKey(request.Name))
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Extension method {0} currently not supported by the server.", request.Name));

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return information;
        }

        /// <summary>
        /// Performs fstatvfs@openssh.com extended request.
        /// </summary>
        /// <param name="handle">The file handle.</param>
        /// <param name="nullOnError">if set to <c>true</c> [null on error].</param>
        /// <returns></returns>
        /// <exception cref="System.NotSupportedException"></exception>
        internal SftpFileSytemInformation RequestFStatVfs(byte[] handle, bool nullOnError = false)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_EXTENDED operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            SftpFileSytemInformation information = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new FStatVfsRequest(this.ProtocolVersion, this.NextRequestId, handle,
                    (response) =>
                    {
                        information = response.GetReply<StatVfsReplyInfo>().Information;

                        wait.Set();
                    },
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                if (!this._supportedExtensions.ContainsKey(request.Name))
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Extension method {0} currently not supported by the server.", request.Name));

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }
            
            if (!nullOnError && exception != null)
            {
                throw exception;
            }

            return information;
        }

        /// <summary>
        /// Performs hardlink@openssh.com extended request.
        /// </summary>
        /// <param name="oldPath">The old path.</param>
        /// <param name="newPath">The new path.</param>
        internal void HardLink(string oldPath, string newPath)
        {
            if (this.ProtocolVersion < 3)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "SSH_FXP_EXTENDED operation is not supported in {0} version that server operates in.", this.ProtocolVersion));
            }

            SshException exception = null;

            using (var wait = new AutoResetEvent(false))
            {
                var request = new HardLinkRequest(this.ProtocolVersion, this.NextRequestId, oldPath, newPath,
                    (response) =>
                    {
                        exception = this.GetSftpException(response);
                        wait.Set();
                    });

                if (!this._supportedExtensions.ContainsKey(request.Name))
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Extension method {0} currently not supported by the server.", request.Name));

                this.SendRequest(request);

                this.WaitOnHandle(wait, this._operationTimeout);
            }

            if (exception != null)
            {
                throw exception;
            }
        }

        #endregion

        /// <summary>
        /// Calculates the optimal size of the buffer to read data from the channel.
        /// </summary>
        /// <param name="bufferSize">The buffer size configured on the client.</param>
        /// <returns>
        /// The optimal size of the buffer to read data from the channel.
        /// </returns>
        internal uint CalculateOptimalReadLength(uint bufferSize)
        {
            // a SSH_FXP_DATA message has 13 bytes of protocol fields:
            // bytes 1 to 4: packet length
            // byte 5: message type
            // bytes 6 to 9: response id
            // bytes 10 to 13: length of payload‏
            //
            // most ssh servers limit the size of the payload of a SSH_MSG_CHANNEL_DATA
            // response to 16 KB; if we requested 16 KB of data, then the SSH_FXP_DATA
            // payload of the SSH_MSG_CHANNEL_DATA message would be too big (16 KB + 13 bytes), and
            // as a result, the ssh server would split this into two responses:
            // one containing 16384 bytes (13 bytes header, and 16371 bytes file data)
            // and one with the remaining 13 bytes of file data
            const uint lengthOfNonDataProtocolFields = 13u;
            var maximumPacketSize = Channel.LocalPacketSize;
            return Math.Min(bufferSize, maximumPacketSize) - lengthOfNonDataProtocolFields;
        }

        /// <summary>
        /// Calculates the optimal size of the buffer to write data on the channel.
        /// </summary>
        /// <param name="bufferSize">The buffer size configured on the client.</param>
        /// <param name="handle">The file handle.</param>
        /// <returns>
        /// The optimal size of the buffer to write data on the channel.
        /// </returns>
        /// <remarks>
        /// Currently, we do not take the remote window size into account.
        /// </remarks>
        internal uint CalculateOptimalWriteLength(uint bufferSize, byte[] handle)
        {
            // 1-4: package length of SSH_FXP_WRITE message
            // 5: message type
            // 6-9: request id
            // 10-13: handle length
            // <handle>
            // 14-21: offset
            // 22-25: data length
            var lengthOfNonDataProtocolFields = 25u + (uint)handle.Length;
            var maximumPacketSize = Channel.RemotePacketSize;
            return Math.Min(bufferSize, maximumPacketSize) - lengthOfNonDataProtocolFields;
        }

        private SshException GetSftpException(SftpStatusResponse response)
        {
            if (response.StatusCode == StatusCodes.Ok)
            {
                return null;
            }
            if (response.StatusCode == StatusCodes.PermissionDenied)
            {
                return new SftpPermissionDeniedException(response.ErrorMessage);
            }
            else if (response.StatusCode == StatusCodes.NoSuchFile)
            {
                return new SftpPathNotFoundException(response.ErrorMessage);
            }
            else
            {
                return new SshException(response.ErrorMessage);
            }
        }

        private void HandleResponse(SftpResponse response)
        {
            SftpRequest request;
            lock (this._requests)
            {
                this._requests.TryGetValue(response.ResponseId, out request);
                if (request != null)
                {
                    this._requests.Remove(response.ResponseId);
                }
            }

            if (request == null)
                throw new InvalidOperationException("Invalid response.");

            request.Complete(response);
        }
    }
}