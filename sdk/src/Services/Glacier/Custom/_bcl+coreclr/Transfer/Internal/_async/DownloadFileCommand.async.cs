﻿/*
 * Copyright 2010-2013 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

using Amazon.Glacier.Model;
using Amazon.Glacier.Transfer.Internal;

using Amazon.Runtime.Internal;

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SQS.Util;

using Amazon.Util;

using ThirdParty.Json.LitJson;
using System.Threading.Tasks;

namespace Amazon.Glacier.Transfer.Internal
{
    internal partial class DownloadFileCommand : IDisposable
    {
        internal async Task ExecuteAsync()
        {
            await this.setupTopicAndQueueAsync();
            try
            {
                var jobId = await initiateJobAsync();
                await processQueueAsync(jobId);
            }
            finally
            {
                await this.tearDownTopicAndQueueAsync();
            }
        }
        async Task processQueueAsync(string jobId)
        {
            Message message = await readNextMessageAsync();
            await processMessageAsync(message, jobId);
            await this.sqsClient.DeleteMessageAsync(new DeleteMessageRequest() { QueueUrl = this.queueUrl, ReceiptHandle = message.ReceiptHandle });
        }

        async Task processMessageAsync(Message message, string jobId)
        {
            var messageJobId = getJobIdFromMessage(message);
            if (messageJobId == null)
                return;

            var command = new DownloadJobCommand(this.manager, this.vaultName, jobId, this.filePath, this.options);
            await command.ExecuteAsync();
        }

        /// <summary>
        /// Poll messages from the queue.  Given the download process takes many hours there is extra
        /// long retry logic.
        /// </summary>
        /// <returns>The next message in the queue;</returns>
        async Task<Message> readNextMessageAsync()
        {
            int retryAttempts = 0;
            var receiveRequest = new ReceiveMessageRequest() { QueueUrl = this.queueUrl, MaxNumberOfMessages = 1 };
            while (true)
            {
                try
                {
                    var receiveResponse = await this.sqsClient.ReceiveMessageAsync(receiveRequest);
                    retryAttempts = 0;

                    if (receiveResponse.Messages.Count == 0)
                    {
                        await Task.Delay((int)(this.options.PollingInterval * 1000 * 60));
                        continue;
                    }

                    return receiveResponse.Messages[0];
                }
                catch (Exception)
                {
                    retryAttempts++;
                    if (retryAttempts <= MAX_OPERATION_RETRY)
                        await Task.Delay(1000 * 60);
                    else
                        throw;
                }
            }
        }

        async Task<string> initiateJobAsync()
        {
            var request = new InitiateJobRequest()
            {
                AccountId = this.options.AccountId,
                VaultName = this.vaultName,
                JobParameters = new JobParameters()
                {
                    ArchiveId = this.archiveId,
                    SNSTopic = topicArn,
                    Type = "archive-retrieval"
                }
            };
            ((Amazon.Runtime.Internal.IAmazonWebServiceRequest)request).AddBeforeRequestHandler(new ArchiveTransferManager.UserAgentPostFix("DownloadArchive").UserAgentRequestEventHandlerSync);

            var response = await this.manager.GlacierClient.InitiateJobAsync(request);
            return response.JobId;
        }

        internal async Task setupTopicAndQueueAsync()
        {
            var guidStr = Guid.NewGuid().ToString("N");
            this.topicArn = (await this.snsClient.CreateTopicAsync(new CreateTopicRequest() { Name = "GlacierDownload-" + guidStr })).TopicArn;
            this.queueUrl = (await this.sqsClient.CreateQueueAsync(new CreateQueueRequest() { QueueName = "GlacierDownload-" + guidStr })).QueueUrl;
            this.queueArn = (await this.sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest() { QueueUrl = this.queueUrl, AttributeNames = new List<string> { SQSConstants.ATTRIBUTE_QUEUE_ARN } })).Attributes[SQSConstants.ATTRIBUTE_QUEUE_ARN];

            await this.snsClient.SubscribeAsync(new SubscribeRequest()
            {
                Endpoint = this.queueArn,
                Protocol = "sqs",
                TopicArn = this.topicArn
            });

            var policy = SQS_POLICY.Replace("{QuereArn}", this.queueArn).Replace("{TopicArn}", this.topicArn);
            var setQueueAttributesRequest = new SetQueueAttributesRequest()
            {
                QueueUrl = this.queueUrl
            };
            setQueueAttributesRequest.Attributes.Add("Policy", policy);

            await this.sqsClient.SetQueueAttributesAsync(setQueueAttributesRequest);
        }

        internal async Task tearDownTopicAndQueueAsync()
        {
            await this.snsClient.DeleteTopicAsync(new DeleteTopicRequest() { TopicArn = this.topicArn });
            await this.sqsClient.DeleteQueueAsync(new DeleteQueueRequest() { QueueUrl = this.queueUrl });
        }
    }
}
