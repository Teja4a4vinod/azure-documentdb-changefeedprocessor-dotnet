﻿//----------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  Licensed under the MIT license.
//----------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Documents.ChangeFeedProcessor.Adapters;
using Microsoft.Azure.Documents.ChangeFeedProcessor.FeedProcessor;
using Microsoft.Azure.Documents.Client;
using Moq;
using Xunit;

namespace Microsoft.Azure.Documents.ChangeFeedProcessor.UnitTests.FeedProcessor
{
    public class PartitionProcessorTests
    {
        private readonly ProcessorSettings processorSettings;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly PartitionProcessor sut;
        private readonly IDocumentClientEx docClient;
        private readonly IDocumentQueryEx<Document> documentQuery;
        private readonly IFeedResponse<Document> feedResponse;
        private readonly IChangeFeedObserver observer;
        private readonly List<Document> documents;

        public PartitionProcessorTests()
        {
            processorSettings = new ProcessorSettings
            {
                CollectionSelfLink = "selfLink",
                FeedPollDelay = TimeSpan.FromMilliseconds(16),
                MaxItemCount = 5,
                PartitionKeyRangeId = "keyRangeId",
                RequestContinuation = "initialToken"
            };

            var document = new Document();
            documents = new List<Document> { document };

            feedResponse = Mock.Of<IFeedResponse<Document>>();
            Mock.Get(feedResponse)
                .Setup(response => response.Count)
                .Returns(documents.Count);
            Mock.Get(feedResponse)
                .Setup(response => response.ResponseContinuation)
                .Returns("token");
            Mock.Get(feedResponse)
                .Setup(response => response.GetEnumerator())
                .Returns(documents.GetEnumerator());

            documentQuery = Mock.Of<IDocumentQueryEx<Document>>();
            Mock.Get(documentQuery)
                .Setup(query => query.HasMoreResults)
                .Returns(false);

            Mock.Get(documentQuery)
                .Setup(query => query.ExecuteNextAsync<Document>(It.Is<CancellationToken>(token => token == cancellationTokenSource.Token)))
                .ReturnsAsync(() => feedResponse)
                .Callback(() => cancellationTokenSource.Cancel());

            docClient = Mock.Of<IDocumentClientEx>();
            Mock.Get(docClient)
                .Setup(ex => ex.CreateDocumentChangeFeedQuery(processorSettings.CollectionSelfLink, It.IsAny<ChangeFeedOptions>()))
                .Returns(documentQuery);

            observer = Mock.Of<IChangeFeedObserver>();
            var checkPointer = new Mock<IPartitionCheckpointer>();
            sut = new PartitionProcessor(observer, docClient, processorSettings, checkPointer.Object);
        }

        [Fact]
        public async Task Run_ShouldThrowException_IfCanceled()
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));
        }

        [Fact]
        public async Task Run_ShouldPassDocumentsToObserver_IfDocumentExists()
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));

            Mock.Get(observer)
                .Verify(feedObserver => feedObserver
                        .ProcessChangesAsync(
                            It.Is<ChangeFeedObserverContext>(context => context.PartitionKeyRangeId == processorSettings.PartitionKeyRangeId),
                            It.Is<IReadOnlyList<Document>>(list => list.SequenceEqual(documents))),
                    Times.Once);
        }

        [Fact]
        public async Task Run_ShouldPassFeedOptionsToQuery_OnCreation()
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));

            Mock.Get(docClient)
                .Verify(d => d.CreateDocumentChangeFeedQuery(
                        It.Is<string>(s => s == processorSettings.CollectionSelfLink),
                        It.Is<ChangeFeedOptions>(options =>
                            options.PartitionKeyRangeId == processorSettings.PartitionKeyRangeId &&
                            options.RequestContinuation == processorSettings.RequestContinuation)),
                    Times.Once);
        }

        [Fact]
        public async Task Run_ShouldPassTheTokenOnce_WhenCanceled()
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));

            Mock.Get(documentQuery)
                .Verify(query => query.ExecuteNextAsync<Document>(It.Is<CancellationToken>(token => token == cancellationTokenSource.Token)), Times.Once);
        }

        [Fact]
        public async Task Run_ShouldContinue_IfDocDBThrowsCanceled()
        {
            Mock.Get(documentQuery)
                .Reset();

            Mock.Get(documentQuery)
                .SetupSequence(query => query.ExecuteNextAsync<Document>(It.Is<CancellationToken>(token => token == cancellationTokenSource.Token)))
                .Throws(new TaskCanceledException("canceled in test"))
                .ReturnsAsync(feedResponse);

            Mock.Get(observer)
                .Setup(feedObserver => feedObserver
                    .ProcessChangesAsync(It.IsAny<ChangeFeedObserverContext>(), It.IsAny<IReadOnlyList<Document>>()))
                .Returns(Task.FromResult(false))
                .Callback(cancellationTokenSource.Cancel);

            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));

            Mock.Get(observer)
                .Verify(feedObserver => feedObserver
                        .ProcessChangesAsync(
                            It.Is<ChangeFeedObserverContext>(context => context.PartitionKeyRangeId == processorSettings.PartitionKeyRangeId),
                            It.Is<IReadOnlyList<Document>>(list => list.SequenceEqual(documents))),
                    Times.Once);
        }

        [Fact]
        public async Task Run_ShouldRetryQuery_IfDocDBThrowsCanceled()
        {
            Mock.Get(documentQuery)
                .Reset();

            Mock.Get(documentQuery)
                .SetupSequence(query => query.ExecuteNextAsync<Document>(It.Is<CancellationToken>(token => token == cancellationTokenSource.Token)))
                .Throws(new TaskCanceledException("canceled in test"))
                .ReturnsAsync(feedResponse);

            Mock.Get(observer)
                .Setup(feedObserver => feedObserver
                    .ProcessChangesAsync(It.IsAny<ChangeFeedObserverContext>(), It.IsAny<IReadOnlyList<Document>>()))
                .Returns(Task.FromResult(false))
                .Callback(cancellationTokenSource.Cancel);

            await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RunAsync(cancellationTokenSource.Token));

            Mock.Get(documentQuery)
                .Verify(query => query.ExecuteNextAsync<Document>(It.Is<CancellationToken>(token => token == cancellationTokenSource.Token)), Times.Exactly(2));

            Mock.Get(observer)
                .Verify(feedObserver => feedObserver
                        .ProcessChangesAsync(
                            It.Is<ChangeFeedObserverContext>(context => context.PartitionKeyRangeId == processorSettings.PartitionKeyRangeId),
                            It.Is<IReadOnlyList<Document>>(list => list.SequenceEqual(documents))),
                    Times.Once);
        }
    }
}