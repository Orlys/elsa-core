﻿using System.Threading;
using System.Threading.Tasks;
using Elsa.Services;
using Microsoft.AspNetCore.Mvc;

namespace Elsa.Server.Api.Endpoints.WorkflowDefinitions
{
    [ApiController]
    [ApiVersion("1")]
    [Route("v{version:apiVersion}/workflow-definitions")]
    [Produces("application/json")]
    public class Post : ControllerBase
    {
        private readonly IWorkflowPublisher _workflowPublisher;

        public Post(IWorkflowPublisher workflowPublisher)
        {
            _workflowPublisher = workflowPublisher;
        }

        [HttpPost]
        public async Task<IActionResult> Handle(SaveWorkflowDefinitionRequest request, ApiVersion apiVersion, CancellationToken cancellationToken)
        {
            var workflowDefinition = await _workflowPublisher.GetDraftAsync(request.WorkflowDefinitionId, cancellationToken);

            if (workflowDefinition == null)
            {
                workflowDefinition = _workflowPublisher.New();

                if (!string.IsNullOrWhiteSpace(request.WorkflowDefinitionId))
                    workflowDefinition.WorkflowDefinitionId = request.WorkflowDefinitionId.Trim();
            }

            workflowDefinition.Activities = request.Activities;
            workflowDefinition.Connections = request.Connections;
            workflowDefinition.Description = request.Description?.Trim();
            workflowDefinition.Name = request.Name?.Trim();
            workflowDefinition.Variables = request.Variables;
            workflowDefinition.IsEnabled = request.Enabled;
            workflowDefinition.IsSingleton = request.IsSingleton;
            workflowDefinition.PersistenceBehavior = request.PersistenceBehavior;
            workflowDefinition.DeleteCompletedInstances = request.DeleteCompletedInstances;

            if (request.Publish)
                await _workflowPublisher.PublishAsync(workflowDefinition, cancellationToken);
            else
                await _workflowPublisher.SaveDraftAsync(workflowDefinition, cancellationToken);

            return CreatedAtAction("Handle", "Get", new { id = workflowDefinition.WorkflowDefinitionId, version = apiVersion.ToString() }, workflowDefinition);
        }
    }
}