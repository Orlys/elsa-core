﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Activities.Signaling;
using Elsa.Bookmarks;
using Elsa.Exceptions;
using Elsa.Models;
using Elsa.Persistence;
using Elsa.Persistence.Specifications;
using Elsa.Persistence.Specifications.WorkflowInstances;
using Elsa.Services;
using Elsa.Triggers;
using MediatR;
using Microsoft.Extensions.Logging;
using Open.Linq.AsyncExtensions;

namespace Elsa.Dispatch.Handlers
{
    public class TriggerWorkflows : IRequestHandler<TriggerWorkflowsRequest, int>
    {
        private readonly IWorkflowInstanceStore _workflowInstanceStore;
        private readonly IBookmarkFinder _bookmarkFinder;
        private readonly ITriggerFinder _triggerFinder;
        private readonly IDistributedLockProvider _distributedLockProvider;
        private readonly IMediator _mediator;
        private readonly ElsaOptions _elsaOptions;
        private readonly ILogger<TriggerWorkflows> _logger;

        public TriggerWorkflows(
            IWorkflowInstanceStore workflowInstanceStore,
            IBookmarkFinder bookmarkFinder,
            ITriggerFinder triggerFinder,
            IDistributedLockProvider distributedLockProvider,
            IMediator mediator,
            ElsaOptions elsaOptions,
            ILogger<TriggerWorkflows> logger)
        {
            _workflowInstanceStore = workflowInstanceStore;
            _bookmarkFinder = bookmarkFinder;
            _triggerFinder = triggerFinder;
            _distributedLockProvider = distributedLockProvider;
            _mediator = mediator;
            _elsaOptions = elsaOptions;
            _logger = logger;
        }

        public async Task<int> Handle(TriggerWorkflowsRequest request, CancellationToken cancellationToken)
        {
            var correlationId = request.CorrelationId;

            if (!string.IsNullOrWhiteSpace(correlationId))
                return await ResumeOrStartCorrelatedWorkflowsAsync(request, cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.WorkflowInstanceId))
                return await ResumeSpecificWorkflowInstanceAsync(request, cancellationToken);

            return await TriggerWorkflowsAsync(request, cancellationToken);
        }

        private async Task<int> TriggerWorkflowsAsync(TriggerWorkflowsRequest request, CancellationToken cancellationToken)
        {
            var bookmarkResultsQuery = await _bookmarkFinder.FindBookmarksAsync(request.ActivityType, request.Bookmark, request.CorrelationId, request.TenantId, cancellationToken);
            var bookmarkResults = bookmarkResultsQuery.ToList();
            var triggeredCount = bookmarkResults.GroupBy(x => x.WorkflowInstanceId).Select(x => x.Key).Distinct().Count();

            await ResumeWorkflowsAsync(bookmarkResults, request.Input, cancellationToken);
            var startedCount = await StartWorkflowsAsync(request, cancellationToken);

            return startedCount + triggeredCount;
        }

        private async Task<int> ResumeSpecificWorkflowInstanceAsync(TriggerWorkflowsRequest request, CancellationToken cancellationToken)
        {
            var bookmarkResultsQuery = await _bookmarkFinder.FindBookmarksAsync(request.ActivityType, request.Bookmark, request.CorrelationId, request.TenantId, cancellationToken);
            bookmarkResultsQuery = bookmarkResultsQuery.Where(x => x.WorkflowInstanceId == request.WorkflowInstanceId);
            var bookmarkResults = bookmarkResultsQuery.ToList();
            var triggeredCount = bookmarkResults.GroupBy(x => x.WorkflowInstanceId).Select(x => x.Key).Distinct().Count();

            await ResumeWorkflowsAsync(bookmarkResults, request.Input, cancellationToken);
            return triggeredCount;
        }

        private async Task<int> ResumeOrStartCorrelatedWorkflowsAsync(TriggerWorkflowsRequest request, CancellationToken cancellationToken)
        {
            var correlationId = request.CorrelationId!;
            var lockKey = correlationId;

            _logger.LogDebug("Acquiring lock on correlation ID {CorrelationId}", correlationId);
            await using (var handle = await _distributedLockProvider.AcquireLockAsync(lockKey, _elsaOptions.DistributedLockTimeout, cancellationToken))
            {
                if (handle == null)
                    throw new LockAcquisitionException($"Failed to acquire a lock on {lockKey}");

                var correlatedWorkflowInstanceCount = !string.IsNullOrWhiteSpace(correlationId)
                    ? await _workflowInstanceStore.CountAsync(new CorrelationIdSpecification<WorkflowInstance>(correlationId).WithStatus(WorkflowStatus.Suspended), cancellationToken)
                    : 0;

                _logger.LogDebug("Found {CorrelatedWorkflowCount} correlated workflows,", correlatedWorkflowInstanceCount);

                if (correlatedWorkflowInstanceCount > 0)
                {
                    _logger.LogDebug("{WorkflowInstanceCount} existing workflows found with correlation ID '{CorrelationId}' will be queued for execution", correlatedWorkflowInstanceCount, correlationId);
                    var bookmarkResults = await _bookmarkFinder.FindBookmarksAsync(request.ActivityType, request.Bookmark, correlationId, request.TenantId, cancellationToken).ToList();
                    await ResumeWorkflowsAsync(bookmarkResults, request.Input, cancellationToken);
                    return correlatedWorkflowInstanceCount;
                }
            }

            return await StartWorkflowsAsync(request, cancellationToken);
        }

        private async Task<int> StartWorkflowsAsync(TriggerWorkflowsRequest request, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Triggering workflows using {ActivityType}", request.ActivityType);

            var filter = request.Trigger;
            var triggers = (await _triggerFinder.FindTriggersAsync(request.ActivityType, filter, request.TenantId, cancellationToken)).ToList();

            foreach (var trigger in triggers)
            {
                var workflowBlueprint = trigger.WorkflowBlueprint;

                await _mediator.Send(
                    new ExecuteWorkflowDefinitionRequest(workflowBlueprint.Id, trigger.ActivityId, request.Input, request.CorrelationId, request.ContextId, workflowBlueprint.TenantId),
                    cancellationToken);
            }

            return triggers.Count;
        }

        private async Task ResumeWorkflowsAsync(IEnumerable<BookmarkFinderResult> results, object? input, CancellationToken cancellationToken)
        {
            foreach (var result in results)
                await _mediator.Send(new ExecuteWorkflowInstanceRequest(result.WorkflowInstanceId, result.ActivityId, input), cancellationToken);
        }
    }
}