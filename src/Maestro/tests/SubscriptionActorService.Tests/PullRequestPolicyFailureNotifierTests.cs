// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using FluentAssertions;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.GitHub.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClientModels = Microsoft.DotNet.Maestro.Client.Models;

namespace SubscriptionActorService.Tests
{
    [TestFixture]
    public class PullRequestPolicyFailureNotifierTests
    {
        protected Mock<IBarClient> BarClient;
        protected Mock<IRemoteFactory> RemoteFactory;
        protected Mock<IHostEnvironment> Env;
        protected Mock<Octokit.IGitHubClient> GithubClient;
        protected Mock<IGitHubTokenProvider> GitHubTokenProvider;
        protected Mock<IGitHubClientFactory> GitHubClientFactory;
        protected IServiceScope Scope;
        protected Remote MockRemote;
        protected ServiceProvider Provider;
        protected List<ClientModels.Subscription> FakeSubscriptions;
        private Dictionary<string, string> PrCommentsMade = new Dictionary<string, string>();
        private const string FakeOrgName = "orgname";
        private const string FakeRepoName = "reponame";

        [SetUp]
        public void PullRequestActorTests_SetUp()
        {
            PrCommentsMade = new Dictionary<string, string>();

            var services = new ServiceCollection();
            FakeSubscriptions = GenerateFakeSubscriptionModels();

            Env = new Mock<IHostEnvironment>(MockBehavior.Strict);
            services.AddSingleton(Env.Object);
            GithubClient = new Mock<Octokit.IGitHubClient>();
            GitHubTokenProvider = new Mock<IGitHubTokenProvider>(MockBehavior.Strict);
            GitHubTokenProvider.Setup(x => x.GetTokenForRepository(It.IsAny<string>())).ReturnsAsync("doesnotmatter");
            GitHubClientFactory = new Mock<IGitHubClientFactory>();
            GitHubClientFactory.Setup(x => x.CreateGitHubClient(It.IsAny<string>()))
                .Returns(GithubClient.Object);
            GithubClient.Setup(ghc => ghc.Issue.Comment.Create(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>()))
                .ReturnsAsync(new Octokit.IssueComment())
                .Callback<string, string, int, string>(RecordGitHubClientComment);

            services.AddLogging();

            BarClient = new Mock<IBarClient>(MockBehavior.Strict);
            BarClient.Setup(b => b.GetSubscriptionAsync(It.IsAny<Guid>()))
                     .Returns((Guid subscriptionToFind) =>
                     {
                         return Task.FromResult(
                             (from subscription in FakeSubscriptions
                              where subscription.Id.Equals(subscriptionToFind)
                              select subscription).FirstOrDefault());
                     });

            MockRemote = new Remote(null, BarClient.Object, NullLogger.Instance);
            RemoteFactory = new Mock<IRemoteFactory>(MockBehavior.Strict);
            RemoteFactory.Setup(m => m.GetBarOnlyRemoteAsync(It.IsAny<ILogger>())).ReturnsAsync(MockRemote);
            Provider = services.BuildServiceProvider();
            Scope = Provider.CreateScope();
        }

        [TestCase()]
        public async Task NotifyACheckFailed()
        {
            // Happy Path: Successfully create a comment, try to do it again, ensure it does not happen.
            var testObject = GetInstance();
            InProgressPullRequest prToTag = GetInProgressPullRequest("https://api.github.com/repos/orgname/reponame/pulls/12345");

            await testObject.TagSourceRepositoryGitHubContactsAsync(prToTag);

            prToTag.SourceRepoNotified.Should().BeTrue();

            // Second time; no second comment should be made. (If it were made, it'd throw)
            await testObject.TagSourceRepositoryGitHubContactsAsync(prToTag);
            PrCommentsMade.Count.Should().Be(1);
            // Spot check some values
            PrCommentsMade[$"{FakeOrgName}/{FakeRepoName}/12345"].Should().Contain(
                $"Notification for subscribed users from https://github.com/{FakeOrgName}/source-repo1");
            foreach (string individual in FakeSubscriptions[0].PullRequestFailureNotificationTags.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // Make sure normalization happens; test includes a user without @.
                string valueToCheck = individual;
                if (!individual.StartsWith('@'))
                    valueToCheck = $"@{valueToCheck}";

                PrCommentsMade[$"{FakeOrgName}/{FakeRepoName}/12345"].Should().Contain(valueToCheck);
            }
        }

        [TestCase()]
        public void MultipleSubscriptionsIncluded()
        {
            // Failure Path: Somehow got called with a batch of subscriptions.  Expect failure.
            // This shouldn't be possible (this object only gets used in the "NonBatched" implementation) 
            // But baking that assumption into a test helps future readers know the intent.
            var testObject = GetInstance();
            InProgressPullRequest prToTag = GetInProgressPullRequest("https://api.github.com/repos/orgname/reponame/pulls/12345", 2);

            Func<Task> execution = async () => await testObject.TagSourceRepositoryGitHubContactsAsync(prToTag);
            execution.Should().Throw<InvalidOperationException> ()
                              .WithMessage("Sequence contains more than one element");
        }

        [TestCase()]
        public async Task NoContactAliasesProvided()
        {
            // "Do nothing" Path: Just don't blow up when a subscription object has no tags.
            var testObject = GetInstance();
            InProgressPullRequest prToTag = GetInProgressPullRequestWithoutTags("https://api.github.com/repos/orgname/reponame/pulls/23456");
            await testObject.TagSourceRepositoryGitHubContactsAsync(prToTag);
            prToTag.SourceRepoNotified.Should().BeFalse();
            PrCommentsMade.Count.Should().Be(0);
        }

        #region Test Helpers

        private InProgressPullRequest GetInProgressPullRequest(string url, int containedSubscriptionCount = 1)
        {
            List<SubscriptionPullRequestUpdate> containedSubscriptions = new List<SubscriptionPullRequestUpdate>();

            for (int i = 0; i < containedSubscriptionCount; i++)
            {
                containedSubscriptions.Add(new SubscriptionPullRequestUpdate()
                {
                    BuildId = 10000 + i,
                    SubscriptionId = FakeSubscriptions[i].Id
                });
            }
            // For the purposes of testing this class, we only need to fill out
            // the "Url" and ContainedSubscriptions fields in InProgressPullRequestObjects
            return new InProgressPullRequest()
            {
                Url = url,
                ContainedSubscriptions = containedSubscriptions,
                SourceRepoNotified = false
            };
        }

        private InProgressPullRequest GetInProgressPullRequestWithoutTags(string url)
        {
            List<SubscriptionPullRequestUpdate> containedSubscriptions = new List<SubscriptionPullRequestUpdate>();

            var tagless = FakeSubscriptions.Where(f => string.IsNullOrEmpty(f.PullRequestFailureNotificationTags)).First();
            containedSubscriptions.Add(new SubscriptionPullRequestUpdate()
            {
                BuildId = 12345,
                SubscriptionId = tagless.Id
            });

            return new InProgressPullRequest()
            {
                Url = url,
                ContainedSubscriptions = containedSubscriptions,
                SourceRepoNotified = false
            };
        }

        private void RecordGitHubClientComment(string owner, string repo, int prIssue, string comment)
        {
            lock (PrCommentsMade)
            {
                // No need to check for existence; if the same comment gets added twice, it'd be a bug
                PrCommentsMade.Add($"{owner}/{repo}/{prIssue}", comment);
            }
        }

        public IPullRequestPolicyFailureNotifier GetInstance()
        {
            PullRequestPolicyFailureNotifier notifier = new PullRequestPolicyFailureNotifier(
                GitHubTokenProvider.Object,
                GitHubClientFactory.Object,
                RemoteFactory.Object,
                Scope.ServiceProvider.GetRequiredService<ILogger<PullRequestPolicyFailureNotifier>>());
            return notifier;
        }

        private List<ClientModels.Subscription> GenerateFakeSubscriptionModels()
        {
            return new List<ClientModels.Subscription>()
            {
                new ClientModels.Subscription(
                    new Guid("35684498-9C08-431F-8E66-8242D7C38598"),
                    true,
                    $"https://github.com/{FakeOrgName}/source-repo1",
                    $"https://github.com/{FakeOrgName}/dest-repo",
                    "fakebranch",
                    "@notifiedUser1;@notifiedUser2;userWithoutAtSign;"),
                new ClientModels.Subscription(
                    new Guid("80B3B6EE-4C9B-46AC-B275-E016E0D5AF41"),
                    true,
                    $"https://github.com/{FakeOrgName}/source-repo2",
                    $"https://github.com/{FakeOrgName}/dest-repo",
                    "fakebranch",
                    "@notifiedUser3;@notifiedUser4"),
                new ClientModels.Subscription(
                    new Guid("1802E0D2-D6BF-4A14-BF4C-B2A292739E59"),
                    true,
                    $"https://github.com/{FakeOrgName}/source-repo2",
                    $"https://github.com/{FakeOrgName}/dest-repo",
                    "fakebranch",
                    string.Empty)
            };
        }
        #endregion
    }
}