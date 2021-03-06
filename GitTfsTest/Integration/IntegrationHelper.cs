﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sep.Git.Tfs.Core;
using Sep.Git.Tfs.Core.TfsInterop;
using Sep.Git.Tfs.VsFake;
using Xunit;
using Xunit.Sdk;

namespace Sep.Git.Tfs.Test.Integration
{
    class IntegrationHelper : IDisposable
    {
        #region manage the work directory

        string _workdir;
        private string Workdir
        {
            get
            {
                if(_workdir == null)
                {
                    _workdir = Path.GetTempFileName();
                    File.Delete(_workdir);
                    Directory.CreateDirectory(_workdir);
                }
                return _workdir;
            }
        }

        public void Dispose()
        {
            if (_workdir != null)
            {
                try
                {
                    Directory.Delete(_workdir);
                    _workdir = null;
                }
                catch (Exception e)
                {
                }
            }
        }

        #endregion

        #region set up vsfake script

        public string FakeScript
        {
            get { return Path.Combine(Workdir, "_fakescript"); }
        }

        public void SetupFake(Action<FakeHistoryBuilder> scripter)
        {
            new Script().Tap(script => scripter(new FakeHistoryBuilder(script))).Save(FakeScript);
        }

        public class FakeHistoryBuilder
        {
            Script _script;
            public FakeHistoryBuilder(Script script)
            {
                _script = script;
            }

            public FakeChangesetBuilder Changeset(int changesetId, string message, DateTime checkinDate)
            {
                var changeset =new ScriptedChangeset
                {
                    Id = changesetId,
                    Comment = message,
                    CheckinDate = checkinDate
                };
                _script.Changesets.Add(changeset);
                return new FakeChangesetBuilder(changeset);
            }
        }

        public class FakeChangesetBuilder
        {
            ScriptedChangeset _changeset;

            public FakeChangesetBuilder(ScriptedChangeset changeset)
            {
                _changeset = changeset;
            }

            public FakeChangesetBuilder Change(TfsChangeType changeType, TfsItemType itemType, string tfsPath, string contents = null)
            {
                _changeset.Changes.Add(new ScriptedChange
                {
                    ChangeType = changeType,
                    ItemType = itemType,
                    RepositoryPath = tfsPath,
                    Content = contents
                });
                return this;
            }
        }

        #endregion

        #region run git-tfs

        public string TfsUrl { get { return "http://does/not/matter"; } }

        public void Run(params string[] args)
        {
            var startInfo = new ProcessStartInfo();
            startInfo.WorkingDirectory = Workdir;
            startInfo.EnvironmentVariables["GIT_TFS_CLIENT"] = "Fake";
            startInfo.EnvironmentVariables[Script.EnvVar] = FakeScript;
            startInfo.EnvironmentVariables["Path"] = CurrentBuildPath + ";" + Environment.GetEnvironmentVariable("Path");
            startInfo.FileName = "cmd";
            startInfo.Arguments = "/c git tfs --debug " + String.Join(" ", args);
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            Console.WriteLine("PATH: " + startInfo.EnvironmentVariables["Path"]);
            Console.WriteLine(">> " + startInfo.FileName + " " + startInfo.Arguments);
            var process = Process.Start(startInfo);
            Console.Out.Write(process.StandardOutput.ReadToEnd());
            process.WaitForExit();
        }

        private string CurrentBuildPath
        {
            get
            {
                var path = new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath;
                return Path.GetDirectoryName(path);
            }
        }

        #endregion

        #region assertions

        public void AssertGitRepo(string repodir)
        {
            var path = Path.Combine(Workdir, repodir);
            Assert.True(Directory.Exists(path), path + " should be a directory");
            Assert.True(Directory.Exists(Path.Combine(path, ".git")), path + " should have a .git dir inside of it");
        }

        public void AssertRef(string repodir, string gitref, string expectedSha)
        {
            AssertEqual(expectedSha, RevParse(repodir, gitref), "Expected " + gitref + " to be " + expectedSha);
        }

        private string RevParse(string repodir, string gitref)
        {
            // This really should delegate to libgit2, which isn't yet a part of GitTfs.
            var gitpath = Path.Combine(Workdir, repodir, ".git");
            var resolved = ReadIfPresent(Path.Combine(gitpath, gitref)) ??
                ReadIfPresent(Path.Combine(gitpath, "refs", "heads", gitref)) ??
                ReadIfPresent(Path.Combine(gitpath, "refs", "remotes", gitref));
            if (resolved != null && resolved.StartsWith("ref:"))
                return RevParse(repodir, resolved.Replace("ref:", "").Trim());
            return resolved;
        }

        private string ReadIfPresent(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        public void AssertEmptyWorkspace(string repodir)
        {
            var entries = new List<string>(Directory.GetFileSystemEntries(Path.Combine(Workdir, repodir)));
            entries.Remove(".");
            entries.Remove("..");
            entries.Remove(".git");
            AssertEqual(new List<string>(), entries, "entries in " + repodir);
        }

        public void AssertCleanWorkspace(string repodir)
        {
            var repo = new LibGit2Sharp.Repository(Path.Combine(Workdir, repodir));
            var status = repo.Index.RetrieveStatus();
            AssertEqual(new List<string>(), status.Select(statusEntry => "" + statusEntry.State + ": " + statusEntry.FilePath).ToList(), "repo status");
        }

        public void AssertFileInWorkspace(string repodir, string file, string contents)
        {
            var path = Path.Combine(Workdir, repodir, file);
            var actual = File.ReadAllText(path, Encoding.UTF8);
            AssertEqual(contents, actual, "Contents of " + path);
        }

        public void AssertCommitMessage(string repodir, string commitish, string message)
        {
            var repo = new LibGit2Sharp.Repository(Path.Combine(Workdir, repodir));
            var commit = LibGit2Sharp.RepositoryExtensions.Lookup<LibGit2Sharp.Commit>(repo, commitish);
            AssertEqual(message, commit.Message, "Commit message of " + commitish);
        }
		
        private void AssertEqual<T>(T expected, T actual, string message)
        {
            try
            {
                Assert.Equal(expected, actual);
            }
            catch (AssertActualExpectedException)
            {
                throw new AssertActualExpectedException(expected, actual, message);
            }
        }

        #endregion
    }
}
