﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Utils;
using SlowTests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Cluster
{
    // tests for RavenDB-13304
    public class ClusterIndexNotificationsTest : ClusterTestBase
    {
        [Fact]
        public async Task ShouldWaitForIndexOfClusterSideEffects()
        {
            using (var store = GetDocumentStore(new Options { DeleteDatabaseOnDispose = true, Path = NewDataPath() }))
            using (var store2 = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User(), "users/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.CountersFor("users/1").Increment("likes");
                    session.SaveChanges();
                }

                var documentDatabase = await GetDatabase(store.Database);
                var testingStuff = documentDatabase.ForTestingPurposesOnly();

                using (testingStuff.CallDuringDocumentDatabaseInternalDispose(() =>
                {
                    Thread.Sleep(12345);
                }))
                {
                    var cts = new CancellationTokenSource();
                    var task = BackgroundWork(store2, cts);

                    await WaitForIndexCreation(store2);

                    await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, true));
                    await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, false));

                    using (var session = store.OpenSession())
                    {
                        session.CountersFor("users/1").Increment("likes");
                        session.SaveChanges();
                    }

                    cts.Cancel();
                    task.Wait();
                }
            }
        }

        [Fact]
        public async Task ShouldThrowTimeoutException()
        {
            DebuggerAttachedTimeout.DisableLongTimespan = true;

            using (var store = GetDocumentStore(new Options { DeleteDatabaseOnDispose = true, Path = NewDataPath() }))
            using (var store2 = GetDocumentStore())
            {
                var documentDatabase = await GetDatabase(store.Database);
                var testingStuff = documentDatabase.ForTestingPurposesOnly();

                using (testingStuff.CallDuringDocumentDatabaseInternalDispose(() =>
                {
                    Thread.Sleep(18 * 1000);
                }))
                {
                    var cts = new CancellationTokenSource();
                    var task = BackgroundWork(store2, cts);

                    await WaitForIndexCreation(store2);

                    var e = await Assert.ThrowsAsync<RavenException>(async () => await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, true)));
                    Assert.True(e.InnerException is TimeoutException);

                    cts.Cancel();
                    task.Wait();
                }
            }
        }

        private static async Task WaitForIndexCreation(DocumentStore store)
        {
            while (store.Maintenance.Send(new GetStatisticsOperation()).CountOfIndexes == 0)
            {
                await Task.Delay(1000);
            }
        }

        private static async Task BackgroundWork(DocumentStore store2, CancellationTokenSource cts)
        {
            while (cts.IsCancellationRequested == false)
            {
                await new UsersIndex().ExecuteAsync(store2);
                await Task.Delay(1000);
            }
        }

        private class UsersIndex : AbstractIndexCreationTask<User>
        {
            public override string IndexName => Guid.NewGuid().ToString();

            public UsersIndex()
            {
                Map = users => 
                    from user in users
                    select new
                    {
                        user.AddressId
                    };
            }
        }
    }
}
