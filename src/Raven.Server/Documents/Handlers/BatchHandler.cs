﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Raven.Client;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Extensions;
using Raven.Server.Documents.Indexes;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron.Exceptions;

namespace Raven.Server.Documents.Handlers
{
    public class BatchHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/bulk_docs", "POST")]
        public async Task BulkDocs()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
            using (var mergedCmd = new MergedBatchCommand{Database = Database})
            {
                if (HttpContext.Request.ContentType != null) // multipart/mixed
                {
                    var boundary = MultipartRequestHelper.GetBoundary(
                        MediaTypeHeaderValue.Parse(HttpContext.Request.ContentType),
                        MultipartRequestHelper.MultipartBoundaryLengthLimit);
                    var reader = new MultipartReader(boundary, RequestBodyStream());
                    for (int i = 0; i < int.MaxValue; i++)
                    {
                        var section = await reader.ReadNextSectionAsync().ConfigureAwait(false);
                        if (section == null)
                            break;

                        var bodyStream = GetBodyStream(section);
                        if (i == 0)
                        {
                            mergedCmd.ParsedCommands = await BatchRequestParser.ParseAsync(ctx, bodyStream, Database.Patcher);
                            continue;
                        }

                        if (mergedCmd.AttachmentStreams == null)
                            mergedCmd.AttachmentStreams = new Queue<MergedBatchCommand.AttachmentStream>();

                        var attachmentStream = new MergedBatchCommand.AttachmentStream();
                        attachmentStream.FileDispose = Database.DocumentsStorage.AttachmentsStorage.GetTempFile(out attachmentStream.File);
                        attachmentStream.Hash = await AttachmentsStorageHelper.CopyStreamToFileAndCalculateHash(ctx, bodyStream, attachmentStream.File, Database.DatabaseShutdown);
                        mergedCmd.AttachmentStreams.Enqueue(attachmentStream);
                    }
                }
                else
                {
                    mergedCmd.ParsedCommands = await BatchRequestParser.ParseAsync(ctx, RequestBodyStream(), Database.Patcher);
                }

                var waitForIndexesTimeout = GetTimeSpanQueryString("waitForIndexesTimeout", required: false);
                if (waitForIndexesTimeout != null)
                    mergedCmd.ModifiedCollections = new HashSet<string>();
                try
                {
                    await Database.TxMerger.Enqueue(mergedCmd);
                }
                catch (ConcurrencyException)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Conflict;
                    throw;
                }

                var waitForReplicasTimeout = GetTimeSpanQueryString("waitForReplicasTimeout", required: false);
                if (waitForReplicasTimeout != null)
                {
                    await WaitForReplicationAsync(waitForReplicasTimeout.Value, mergedCmd);
                }

                if (waitForIndexesTimeout != null)
                {
                    await
                        WaitForIndexesAsync(waitForIndexesTimeout.Value, mergedCmd.LastEtag,
                            mergedCmd.ModifiedCollections);
                }

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(ctx, ResponseBodyStream()))
                {
                    ctx.Write(writer, new DynamicJsonValue
                    {
                        ["Results"] = mergedCmd.Reply
                    });
                }
            }
        }

        private async Task WaitForReplicationAsync(TimeSpan waitForReplicasTimeout, MergedBatchCommand mergedCmd)
        {
            int numberOfReplicasToWaitFor;
            var numberOfReplicasStr = GetStringQueryString("numberOfReplicasToWaitFor", required: false) ?? "1";
            if (numberOfReplicasStr == "majority")
            {
                numberOfReplicasToWaitFor = Database.ReplicationLoader.GetSizeOfMajority();
            }
            else
            {
                if (int.TryParse(numberOfReplicasStr, out numberOfReplicasToWaitFor) == false)
                    ThrowInvalidInteger("numberOfReplicasToWaitFor", numberOfReplicasStr);
            }
            var throwOnTimeoutInWaitForReplicas = GetBoolValueQueryString("throwOnTimeoutInWaitForReplicas") ?? true;

            var waitForReplicationAsync = Database.ReplicationLoader.WaitForReplicationAsync(
                numberOfReplicasToWaitFor,
                waitForReplicasTimeout,
                mergedCmd.LastEtag);

            var replicatedPast = await waitForReplicationAsync;
            if (replicatedPast < numberOfReplicasToWaitFor && throwOnTimeoutInWaitForReplicas)
            {
                throw new TimeoutException(
                    $"Could not verify that etag {mergedCmd.LastEtag} was replicated to {numberOfReplicasToWaitFor} servers in {waitForReplicasTimeout}. So far, it only replicated to {replicatedPast}");
            }
        }

        private async Task WaitForIndexesAsync(TimeSpan timeout, long lastEtag, HashSet<string> modifiedCollections)
        {
            // waitForIndexesTimeout=timespan & waitForIndexThrow=false (default true)
            // waitForspecificIndex=specific index1 & waitForspecificIndex=specific index 2

            if (modifiedCollections.Count == 0)
                return;

            var throwOnTimeout = GetBoolValueQueryString("waitForIndexThrow", required: false) ?? true;

            var indexesToWait = new List<WaitForIndexItem>();

            var indexesToCheck = GetImpactedIndexesToWaitForToBecomeNonStale(modifiedCollections);

            if (indexesToCheck.Count == 0)
                return;

            var sp = Stopwatch.StartNew();

            // we take the awaiter _before_ the indexing transaction happens, 
            // so if there are any changes, it will already happen to it, and we'll 
            // query the index again. This is important because of: 
            // http://issues.hibernatingrhinos.com/issue/RavenDB-5576
            foreach (var index in indexesToCheck)
            {
                var indexToWait = new WaitForIndexItem
                {
                    Index = index,
                    IndexBatchAwaiter = index.GetIndexingBatchAwaiter(),
                    WaitForIndexing = new AsyncWaitForIndexing(sp, timeout, index)
                };

                indexesToWait.Add(indexToWait);
            }

            DocumentsOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                while (true)
                {
                    var hadStaleIndexes = false;

                    using (context.OpenReadTransaction())
                    {
                        foreach (var waitForIndexItem in indexesToWait)
                        {
                            if (waitForIndexItem.Index.IsStale(context, lastEtag) == false)
                                continue;

                            hadStaleIndexes = true;

                            await waitForIndexItem.WaitForIndexing.WaitForIndexingAsync(waitForIndexItem.IndexBatchAwaiter);

                            if (waitForIndexItem.WaitForIndexing.TimeoutExceeded && throwOnTimeout)
                            {
                                throw new TimeoutException(
                                    $"After waiting for {sp.Elapsed}, could not verify that {indexesToCheck.Count} " +
                                    $"indexes has caught up with the changes as of etag: {lastEtag}");
                            }
                        }
                    }

                    if (hadStaleIndexes == false)
                        return;
                }
            }
        }

        private List<Index> GetImpactedIndexesToWaitForToBecomeNonStale(HashSet<string> modifiedCollections)
        {
            var indexesToCheck = new List<Index>();

            var specifiedIndexesQueryString = HttpContext.Request.Query["waitForSpecificIndexs"];

            if (specifiedIndexesQueryString.Count > 0)
            {
                var specificIndexes = specifiedIndexesQueryString.ToHashSet();
                foreach (var index in Database.IndexStore.GetIndexes())
                {
                    if (specificIndexes.Contains(index.Name))
                    {
                        if (index.Collections.Count == 0 || index.Collections.Overlaps(modifiedCollections))
                            indexesToCheck.Add(index);
                    }
                }
            }
            else
            {
                foreach (var index in Database.IndexStore.GetIndexes())
                {
                    if (index.Collections.Count == 0 || index.Collections.Overlaps(modifiedCollections))
                        indexesToCheck.Add(index);
                }
            }
            return indexesToCheck;
        }

        public class MergedBatchCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
        {
            public DynamicJsonArray Reply;
            public ArraySegment<BatchRequestParser.CommandData> ParsedCommands;
            public Queue<AttachmentStream> AttachmentStreams;
            public DocumentDatabase Database;
            public long LastEtag;

            public HashSet<string> ModifiedCollections;

            public override string ToString()
            {
                var sb = new StringBuilder($"{ParsedCommands.Count} commands").AppendLine();
                if (AttachmentStreams != null)
                {
                    sb.AppendLine($"{AttachmentStreams.Count} attachment streams.");
                }
                foreach (var cmd in ParsedCommands)
                {
                    sb.Append("\t")
                        .Append(cmd.Type)
                        .Append(" ")
                        .Append(cmd.Key)
                        .AppendLine();
                }
                return sb.ToString();
            }

            public override int Execute(DocumentsOperationContext context)
            {
                Reply = new DynamicJsonArray();
                for (int i = ParsedCommands.Offset; i < ParsedCommands.Count; i++)
                {
                    var cmd = ParsedCommands.Array[ParsedCommands.Offset + i];
                    switch (cmd.Type)
                    {
                        case CommandType.PUT:
                        {
                            var putResult = Database.DocumentsStorage.Put(context, cmd.Key, cmd.Etag, cmd.Document);

                            context.DocumentDatabase.HugeDocuments.AddIfDocIsHuge(cmd.Key, cmd.Document.Size);

                            LastEtag = putResult.Etag;

                            ModifiedCollections?.Add(putResult.Collection.Name);

                            var changeVector = new DynamicJsonArray();
                            if (putResult.ChangeVector != null)
                            {
                                foreach (var entry in putResult.ChangeVector)
                                    changeVector.Add(entry.ToJson());
                            }

                            // Make sure all the metadata fields are always been add
                            var putReply = new DynamicJsonValue
                            {
                                ["Type"] = CommandType.PUT.ToString(),
                                [Constants.Documents.Metadata.Id] = putResult.Key,
                                [Constants.Documents.Metadata.Etag] = putResult.Etag,
                                [Constants.Documents.Metadata.Collection] = putResult.Collection.Name,
                                [Constants.Documents.Metadata.ChangeVector] = changeVector,
                                [Constants.Documents.Metadata.LastModified] = putResult.LastModified,
                            };

                            if (putResult.Flags != DocumentFlags.None)
                                putReply[Constants.Documents.Metadata.Flags] = putResult.Flags;

                            Reply.Add(putReply);
                        }
                            break;
                        case CommandType.PATCH:
                            // TODO: Move this code out of the merged transaction
                            // TODO: We should have an object that handles this externally, 
                            // TODO: and apply it there

                            var patchResult = Database.Patcher.Apply(context, cmd.Key, cmd.Etag, cmd.Patch, cmd.PatchIfMissing, skipPatchIfEtagMismatch: false, debugMode: false);
                            if (patchResult.ModifiedDocument != null)
                                context.DocumentDatabase.HugeDocuments.AddIfDocIsHuge(cmd.Key, patchResult.ModifiedDocument.Size);
                            if (patchResult.Etag != null)
                                LastEtag = patchResult.Etag.Value;
                            if (patchResult.Collection != null)
                                ModifiedCollections?.Add(patchResult.Collection);

                            Reply.Add(new DynamicJsonValue
                            {
                                ["Key"] = cmd.Key,
                                ["Etag"] = patchResult.Etag,
                                ["Type"] = CommandType.PATCH.ToString(),
                                ["PatchStatus"] = patchResult.Status.ToString(),
                            });
                            break;
                        case CommandType.DELETE:
                            
                            if (cmd.KeyPrefixed == false)
                            {
                                var deleted = Database.DocumentsStorage.Delete(context, cmd.Key, cmd.Etag);

                                if (deleted != null)
                                {
                                    LastEtag = deleted.Value.Etag;
                                    ModifiedCollections?.Add(deleted.Value.Collection.Name);
                                }

                                Reply.Add(new DynamicJsonValue
                                {
                                    ["Key"] = cmd.Key,
                                    ["Type"] = CommandType.DELETE.ToString(),
                                    ["Deleted"] = deleted != null
                                });
                            }
                            else
                            {
                                var deleteResults = Database.DocumentsStorage.DeleteDocumentsStartingWith(context, cmd.Key);

                                for (var j = 0; j < deleteResults.Count; j++)
                                {
                                    LastEtag = deleteResults[j].Etag;
                                    ModifiedCollections?.Add(deleteResults[j].Collection.Name);
                                }

                                Reply.Add(new DynamicJsonValue
                                {
                                    ["Key"] = cmd.Key,
                                    ["Type"] = CommandType.DELETE.ToString(),
                                    ["Deleted"] = deleteResults.Count > 0
                                });
                            }
                            
                            break;
                        case CommandType.AttachmentPUT:
                            using (var attachmentStream = AttachmentStreams.Dequeue())
                            {
                                var attachmentPutResult = Database.DocumentsStorage.AttachmentsStorage.PutAttachment(context, cmd.Key, cmd.Name, cmd.ContentType, attachmentStream.Hash, cmd.Etag, attachmentStream.File);
                                LastEtag = attachmentPutResult.Etag;

                                // Make sure all the metadata fields are always been add
                                var attachmentPutReply = new DynamicJsonValue
                                {
                                    ["Type"] = CommandType.AttachmentPUT.ToString(),
                                    [Constants.Documents.Metadata.Id] = attachmentPutResult.DocumentId,
                                    ["Name"] = attachmentPutResult.Name,
                                    [Constants.Documents.Metadata.Etag] = attachmentPutResult.Etag,
                                    ["Hash"] = attachmentPutResult.Hash,
                                    ["ContentType"] = attachmentPutResult.ContentType,
                                    ["Size"] = attachmentPutResult.Size,
                                };

                                Reply.Add(attachmentPutReply);
                            }

                            break;
                    }
                }
                return Reply.Count;
            }

            public void Dispose()
            {
                foreach (var cmd in ParsedCommands)
                {
                    cmd.Document?.Dispose();
                }
                BatchRequestParser.ReturnBuffer(ParsedCommands);
            }

            public struct AttachmentStream : IDisposable
            {
                public string Hash;

                public FileStream File;
                public AttachmentsStorage.ReleaseTempFile FileDispose;

                public void Dispose()
                {
                    FileDispose.Dispose();
                }
            }
        }

        private class WaitForIndexItem
        {
            public Index Index;
            public AsyncManualResetEvent.FrozenAwaiter IndexBatchAwaiter;
            public AsyncWaitForIndexing WaitForIndexing;
        }
    }
}