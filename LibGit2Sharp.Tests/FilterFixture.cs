﻿using System;
using System.Collections.Generic;
using System.IO;
using LibGit2Sharp.Tests.TestHelpers;
using Xunit;

namespace LibGit2Sharp.Tests
{
    public class FilterFixture : BaseFixture
    {
        readonly Action<Stream, Stream> successCallback = (reader, writer) =>
        {
            reader.CopyTo(writer);
        };

        private const string FilterName = "the-filter";
        readonly List<FilterAttributeEntry> attributes = new List<FilterAttributeEntry> { new FilterAttributeEntry("test") };

        [Fact]
        public void CanRegisterFilterWithSingleAttribute()
        {
            var filter = new EmptyFilter(FilterName, attributes);
            Assert.Equal(attributes, filter.Attributes);
        }

        [Fact]
        public void CanRegisterAndUnregisterTheSameFilter()
        {
            var filter = new EmptyFilter(FilterName + 1, attributes);

            var registration = GlobalSettings.RegisterFilter(filter);
            GlobalSettings.DeregisterFilter(registration);

            var secondRegistration = GlobalSettings.RegisterFilter(filter);
            GlobalSettings.DeregisterFilter(secondRegistration);
        }

        [Fact]
        public void CanRegisterAndDeregisterAfterGarbageCollection()
        {
            var filter = new EmptyFilter(FilterName + 2, attributes);
            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            GC.Collect();

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void SameFilterIsEqual()
        {
            var filter = new EmptyFilter(FilterName + 3, attributes);
            Assert.Equal(filter, filter);
        }

        [Fact]
        public void InitCallbackNotMadeWhenFilterNeverUsed()
        {
            bool called = false;
            Action initializeCallback = () =>
            {
                called = true;
            };

            var filter = new FakeFilter(FilterName + 11, attributes,
                successCallback,
                successCallback,
                initializeCallback);

            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            Assert.False(called);

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void InitCallbackMadeWhenUsingTheFilter()
        {
            bool called = false;
            Action initializeCallback = () =>
            {
                called = true;
            };

            var filter = new FakeFilter(FilterName + 12, attributes,
                successCallback,
                successCallback,
                initializeCallback);

            var filterRegistration = GlobalSettings.RegisterFilter(filter);
            Assert.False(called);

            string repoPath = InitNewRepository();
            using (var repo = CreateTestRepository(repoPath))
            {
                StageNewFile(repo);
                Assert.True(called);
            }

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void WhenStagingFileApplyIsCalledWithCleanForCorrectPath()
        {
            string repoPath = InitNewRepository();
            bool called = false;

            Action<Stream, Stream> clean = (reader, writer) =>
            {
                called = true;
                reader.CopyTo(writer);
            };
            var filter = new FakeFilter(FilterName + 15, attributes, clean);

            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            using (var repo = CreateTestRepository(repoPath))
            {
                StageNewFile(repo);
                Assert.True(called);
            }

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void CleanFilterWritesOutputToObjectTree()
        {
            const string decodedInput = "This is a substitution cipher";
            const string encodedInput = "Guvf vf n fhofgvghgvba pvcure";

            string repoPath = InitNewRepository();

            Action<Stream, Stream> cleanCallback = SubstitutionCipherFilter.RotateByThirteenPlaces;

            var filter = new FakeFilter(FilterName + 16, attributes, cleanCallback);

            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            using (var repo = CreateTestRepository(repoPath))
            {
                FileInfo expectedFile = StageNewFile(repo, decodedInput);
                var commit = repo.Commit("Clean that file");

                var blob = (Blob)commit.Tree[expectedFile.Name].Target;

                var textDetected = blob.GetContentText();
                Assert.Equal(encodedInput, textDetected);
            }

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void WhenCheckingOutAFileFileSmudgeWritesCorrectFileToWorkingDirectory()
        {
            const string decodedInput = "This is a substitution cipher";
            const string encodedInput = "Guvf vf n fhofgvghgvba pvcure";

            const string branchName = "branch";
            string repoPath = InitNewRepository();

            Action<Stream, Stream> smudgeCallback = SubstitutionCipherFilter.RotateByThirteenPlaces;

            var filter = new FakeFilter(FilterName + 17, attributes, null, smudgeCallback);
            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            FileInfo expectedFile = CheckoutFileForSmudge(repoPath, branchName, encodedInput);

            string combine = Path.Combine(repoPath, "..", expectedFile.Name);
            string readAllText = File.ReadAllText(combine);
            Assert.Equal(decodedInput, readAllText);

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        [Fact]
        public void CanFilterLargeFiles()
        {
            const int ContentLength = 128 * 1024 * 1024;
            const char ContentValue = 'x';

            char[] content = (new string(ContentValue, 1024)).ToCharArray();

            string repoPath = InitNewRepository();

            var filter = new FileExportFilter("exportFilter", attributes);
            var filterRegistration = GlobalSettings.RegisterFilter(filter);

            string filePath = Path.Combine(Directory.GetParent(repoPath).Parent.FullName, Guid.NewGuid().ToString() + ".blob");
            FileInfo contentFile = new FileInfo(filePath);
            using (var writer = new StreamWriter(contentFile.OpenWrite()) { AutoFlush = true })
            {
                for (int i = 0; i < ContentLength / content.Length; i++)
                {
                    writer.Write(content);
                }
            }

            string attributesPath = Path.Combine(Directory.GetParent(repoPath).Parent.FullName, ".gitattributes");
            FileInfo attributesFile = new FileInfo(attributesPath);

            string configPath = CreateConfigurationWithDummyUser(Constants.Signature);
            var repositoryOptions = new RepositoryOptions { GlobalConfigurationLocation = configPath };

            using (Repository repo = new Repository(repoPath, repositoryOptions))
            {
                File.WriteAllText(attributesPath, "*.blob filter=test");
                repo.Stage(attributesFile.Name);
                repo.Stage(contentFile.Name);
                repo.Commit("test");
                contentFile.Delete();
                repo.Checkout("HEAD", new CheckoutOptions() { CheckoutModifiers = CheckoutModifiers.Force });
            }

            contentFile = new FileInfo(filePath);
            Assert.True(contentFile.Exists, "Contents not restored correctly by forced checkout.");
            using (StreamReader reader = contentFile.OpenText())
            {
                int totalRead = 0;
                char[] block = new char[1024];
                int read;
                while ((read = reader.Read(block, 0, block.Length)) > 0)
                {
                    Assert.True(CharArrayAreEqual(block, content, read));
                    totalRead += read;
                }

                Assert.Equal(ContentLength, totalRead);
            }

            contentFile.Delete();

            GlobalSettings.DeregisterFilter(filterRegistration);
        }

        private unsafe bool CharArrayAreEqual(char[] array1, char[] array2, int count)
        {
            if (ReferenceEquals(array1, array2))
            {
                return true;
            }
            if (ReferenceEquals(array1, null) || ReferenceEquals(null, array2))
            {
                return false;
            }
            if (array1.Length < count || array2.Length < count)
            {
                return false;
            }

            int len = count * sizeof(char);
            int cnt = len / sizeof(long);

            fixed (char* c1 = array1, c2 = array2)
            {
                long* p1 = (long*)c1,
                      p2 = (long*)c2;

                for (int i = 0; i < cnt; i++)
                {
                    if (p1[i] != p2[i])
                    {
                        return false;
                    }
                }

                byte* b1 = (byte*)c1,
                      b2 = (byte*)c2;

                for (int i = len * sizeof(long); i < len; i++)
                {
                    if (b1[i] != b2[i])
                    {
                        return false;
                    }
                }
            }

            return true;
        }


        private FileInfo CheckoutFileForSmudge(string repoPath, string branchName, string content)
        {
            FileInfo expectedPath;
            using (var repo = CreateTestRepository(repoPath))
            {
                StageNewFile(repo, content);

                repo.Commit("Initial commit");

                expectedPath = CommitFileOnBranch(repo, branchName, content);

                repo.Checkout("master");

                repo.Checkout(branchName);
            }
            return expectedPath;
        }

        private static FileInfo CommitFileOnBranch(Repository repo, string branchName, String content)
        {
            var branch = repo.CreateBranch(branchName);
            repo.Checkout(branch.FriendlyName);

            FileInfo expectedPath = StageNewFile(repo, content);
            repo.Commit("Commit");
            return expectedPath;
        }

        private static FileInfo StageNewFile(IRepository repo, string contents = "null")
        {
            string newFilePath = Touch(repo.Info.WorkingDirectory, Guid.NewGuid() + ".txt", contents);
            var stageNewFile = new FileInfo(newFilePath);
            repo.Stage(newFilePath);
            return stageNewFile;
        }

        private Repository CreateTestRepository(string path)
        {
            string configPath = CreateConfigurationWithDummyUser(Constants.Signature);
            var repositoryOptions = new RepositoryOptions { GlobalConfigurationLocation = configPath };
            var repository = new Repository(path, repositoryOptions);
            CreateAttributesFile(repository, "* filter=test");
            return repository;
        }

        private static void CreateAttributesFile(IRepository repo, string attributeEntry)
        {
            Touch(repo.Info.WorkingDirectory, ".gitattributes", attributeEntry);
        }

        class EmptyFilter : Filter
        {
            public EmptyFilter(string name, IEnumerable<FilterAttributeEntry> attributes)
                : base(name, attributes)
            { }
        }

        class FakeFilter : Filter
        {
            private readonly Action<Stream, Stream> cleanCallback;
            private readonly Action<Stream, Stream> smudgeCallback;
            private readonly Action initCallback;

            public FakeFilter(string name, IEnumerable<FilterAttributeEntry> attributes,
                Action<Stream, Stream> cleanCallback = null,
                Action<Stream, Stream> smudgeCallback = null,
                Action initCallback = null)
                : base(name, attributes)
            {
                this.cleanCallback = cleanCallback;
                this.smudgeCallback = smudgeCallback;
                this.initCallback = initCallback;
            }

            protected override void Clean(string path, string root, Stream input, Stream output)
            {
                if (cleanCallback == null)
                {
                    base.Clean(path, root, input, output);
                }
                else
                {
                    cleanCallback(input, output);
                }
            }

            protected override void Smudge(string path, string root, Stream input, Stream output)
            {
                if (smudgeCallback == null)
                {
                    base.Smudge(path, root, input, output);
                }
                else
                {
                    smudgeCallback(input, output);
                }
            }

            protected override void Initialize()
            {
                if (initCallback == null)
                {
                    base.Initialize();
                }
                else
                {
                    initCallback();
                }
            }
        }
    }
}