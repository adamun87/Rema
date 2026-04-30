# Change Validation Workflow

Analyze branch changes, run buddy builds, fix failures, deploy to a single INT stamp, and validate — a complete change validation loop.

## When to Use

Invoke this workflow when the user asks to:
- Validate changes in a branch or worktree
- Run a buddy build and deploy to INT
- Test changes before creating a PR

## Prerequisites

- Service project onboarded with repo path and pipeline configs
- Git worktree or branch with changes
- ADO connection active

## Workflow Steps

### 1. Analyze Changes

```
1. Identify the current branch and repo
2. Use git diff to understand what changed:
   - Code changes (services, libraries)
   - Config changes (deployment manifests, settings)
   - Schema/database changes
   - Dependency updates
3. Classify risk level based on change scope
4. Identify which service project(s) are affected
5. Look up the project's pipeline configs — they're already onboarded, don't re-scan
```

### 2. Propose Validation Plan

```
1. Call `rema_propose_deployment_plan` with:
   - Build version: current branch name
   - Stages: Build → Deploy INT → Validate
   - Target clusters: ONE INT stamp only
   - Excluded: all other INT stamps, all production
2. Wait for user confirmation or adjustment
3. Call `rema_register_operation` with:
   - goal: "Validate {branch} changes for {ServiceName}"
   - kind: "Change Validation"
```

### 3. Buddy Build Loop

```
REPEAT:
  1. Trigger buddy build via the project's configured build pipeline
  2. Call `rema_update_operation` with currentStep: "Building..."
  3. Wait for build to complete
  4. IF BUILD PASSES → go to step 4
  5. IF BUILD FAILS:
     a. Analyze build logs to identify the failure
     b. Call `rema_update_operation` with currentStep describing the failure
     c. IF fix is clear and safe:
        - Apply the fix
        - Log what was changed and why
        - Retry the build
     d. IF fix is unclear:
        - Present the failure with full context
        - Ask the user: "How should I fix this?"
        - Wait for guidance
        - Apply fix and retry
UNTIL build passes
```

### 4. Deploy to Single INT Stamp

```
1. Call `rema_update_operation` with currentStep: "Deploying to INT"
2. Identify the correct INT deployment pipeline from project config
3. Deploy to a SINGLE stamp only — do NOT deploy to all INT stamps
4. Consider cross-repo dependencies:
   - Different repos may have different deployment pipelines
   - Some services have dependency ordering requirements
   - Ask the user if deployment order is unclear
5. Wait for deployment to complete
6. IF deployment fails → analyze, fix, and retry (same loop as build)
```

### 5. INT Validation

```
1. Call `rema_update_operation` with currentStep: "Validating deployment"
2. Wait 5 minutes for metrics to stabilize
3. Run validation checks:
   - Use project's configured health queries
   - Use in-repo validation tools/skills/agents
   - Check error rates, latency, version deployment
4. Present results with rich visualization:
   - Collapsible query blocks
   - Charts for metrics
   - Health cards for summary
5. IF validation passes → go to step 6
6. IF issues found:
   a. Present the issue with telemetry evidence
   b. IF fix is clear → apply fix, redeploy, re-validate
   c. IF unclear → ask the user with supporting data
   d. Loop until validation passes or user decides to stop
```

### 6. Final Report

```
1. Call `rema_update_operation` with status: "Completed"
2. Present comprehensive report:

   ## Change Validation Report
   
   ### Changes Analyzed
   - List of changed files and their classification
   
   ### Build
   - Build attempts and outcomes
   - Issues encountered and how they were resolved
   
   ### Deployment
   - Target INT stamp
   - Deployment status
   
   ### Validation
   - Health check results with telemetry evidence
   - Metrics comparison (before vs after)
   
   ### Issues Resolved
   - Each issue encountered during the process
   - The fix applied
   - Supporting evidence
   
   ### Next Steps
   - PR readiness assessment
   - Additional INT stamps to validate (if needed)
   - Production deployment readiness
```

## Key Rules

- NEVER deploy to all INT stamps — pick ONE for initial validation
- NEVER assume — if unclear about a failure, ask the user
- Prefer local environment validation over INT when possible
- Use the project's EXISTING pipeline configs — don't scan the repo
- Log every issue and fix for the final report
- Update dashboard operation progress at every step
- Show telemetry evidence for all health assessments
- Consider cross-repo pipeline dependencies
