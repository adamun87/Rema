# Release Management Workflow

Track a production release end-to-end: monitor pipeline stages, validate health at each ring, get approval for next ring, and report completion.

## When to Use

Invoke this workflow when the user asks to:
- Track a production release / deployment
- Monitor a pipeline through its deployment rings
- Manage a staged rollout

## Prerequisites

- Service project must be onboarded with pipeline configurations
- ADO connection must be active
- Health queries configured (optional but recommended)

## Workflow Steps

### 1. Initialize Release Tracking

```
1. Identify the service project, pipeline, and build version
2. Call `rema_propose_deployment_plan` with:
   - Target stages from pipeline config
   - Target clusters/rings
   - Excluded targets (if any)
3. Wait for user confirmation
4. Call `rema_register_operation` with:
   - goal: "Release {ServiceName} v{Version} to {target rings}"
   - kind: "Deployment"
5. Start tracking the pipeline run
```

### 2. Monitor Each Ring

For each deployment ring/stage:

```
1. Check pipeline status using ADO tools
2. Call `rema_update_operation` with current stage and progress
3. If stage is running → wait and re-check periodically
4. If stage needs approval → notify user (see step 3)
5. If stage fails → alert user immediately (see step 4)
6. If stage succeeds → run health validation (see step 5)
```

### 3. Request Approval

When a stage needs user approval:

```
1. Present the current deployment status with evidence:
   - Show completed stages and their health status
   - Show the next stage and what it targets
   - Include telemetry evidence (charts, health cards)
2. Call `rema_update_operation` with status "WaitingForInput"
3. Ask the user explicitly: "Approve deployment to {next ring}?"
4. Wait for response before proceeding
```

### 4. Handle Failures

When a stage fails:

```
1. Immediately flag in chat with failure details
2. Call `rema_update_operation` with currentStep describing the failure
3. Gather evidence:
   - ADO build logs
   - Error messages
   - Telemetry showing the impact
4. Present options:
   - Investigate further
   - Retry the stage
   - Rollback
5. Wait for user decision
```

### 5. Health Validation

After each successful stage:

```
1. Wait 5-10 minutes for metrics to stabilize
2. Run configured health queries (if available)
3. Check key metrics:
   - Error rate (compare to pre-deployment baseline)
   - P99 latency
   - Request volume
   - Version deployment confirmation
4. Present results using rich visualization:
   - Collapsible query block
   - Line chart for time-series metrics
   - Health card with pass/fail summary
   - Confidence meter for overall assessment
5. If healthy → recommend proceeding to next ring
6. If degraded → flag with evidence and suggest investigation
```

### 6. Completion

When all rings are deployed:

```
1. Call `rema_update_operation` with status "Completed"
2. Present final summary:
   - Version deployed across all rings
   - Health status at each ring
   - Issues encountered and resolutions
   - Total deployment duration
```

## Multi-Release Support

This workflow can track multiple releases simultaneously. Each release gets its own dashboard operation with independent progress tracking.

## Key Rules

- NEVER proceed to the next ring without explicit user approval
- ALWAYS validate health before requesting approval for the next ring
- Use project's configured pipelines — don't scan the repo to find them
- Prefer project's health queries for validation
- Show telemetry evidence for every health assessment
- Update the dashboard operation at every significant step
