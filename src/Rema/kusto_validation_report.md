# Sherlock Kusto Validation Report

**Generated:** 2026-04-30 13:46:52  
**Environment:** Rema Development Environment  
**Validation Target:** Sherlock Conversation Discovery via Kusto

## Configuration Under Test

- **Cluster:** `https://sherlockdiag-tel-int.eastus.kusto.windows.net`
- **Database:** `SherlockTelemetry`
- **Region Filter:** `eastus2euap`
- **Authentication:** Azure PowerShell (adau@microsoft.com)
- **Subscription:** Alerts_SmartAlerts_Engine_ALL_INT_00

## Test Results Summary

| Test | Status | Result |
|------|--------|---------|
| 1. Basic Connectivity | ❌ FAILED | 401 Unauthorized |
| 2. Schema Validation | ⚠️ BLOCKED | Cannot connect |
| 3. Conversation Discovery | ⚠️ BLOCKED | Cannot execute queries |
| 4. Regional Filtering | ⚠️ BLOCKED | Cannot validate filters |
| 5. Performance Test | ⚠️ BLOCKED | Cannot measure timing |

## Validation Confidence Level

- **Previous:** 80% (before access testing)
- **Current:** 20% (access verification failed)

## Access Issues Identified

### 1. Authentication/Authorization
- Current Azure context lacks access to the Kusto cluster
- May require different subscription or RBAC permissions
- Cluster may not exist in current subscription scope

### 2. Cluster Discovery
- Cluster not found in expected resource groups
- Searched: sherlock-diagnostics, sherlock-diagnostics-int, sherlock, sherlockdiag, analytics, telemetry-int
- May be in different subscription or region

## Required Tests (Pending Access)

### Test 1: Basic Connectivity
```kusto
print "Kusto connectivity test completed at: ", now()
```

### Test 2: Database Schema Validation
```kusto
traces
| getschema 
| where ColumnName in ("timestamp", "message", "customDimensions")
| project ColumnName, ColumnType
```

### Test 3: Conversation Discovery Query
```kusto
declare query_parameters(after:datetime, before:datetime, region:string, maxResults:int);
traces
| where timestamp > ago(24h)
| where message startswith "Conversation completed"
| extend ConversationId = tostring(customDimensions["Conversation.Id"]),
         ConversationStatus = coalesce(tostring(customDimensions["Conversation.Status"]), tostring(customDimensions["Status"])),
         TelemetryRegion = tostring(customDimensions["Region"])
| where isnotempty(ConversationId)
| where TelemetryRegion =~ "eastus2euap"
| where ConversationStatus == "Completed"
| summarize CompletedAt = max(timestamp) by ConversationId
| order by CompletedAt asc, ConversationId asc
| take 10
```

### Test 4: Regional Scoping Validation
```kusto
traces
| where timestamp > ago(24h)
| where message startswith "Conversation completed"
| extend TelemetryRegion = tostring(customDimensions["Region"])
| where isnotempty(TelemetryRegion)
| summarize Count = count() by TelemetryRegion
| order by Count desc
```

### Test 5: Performance Test
```kusto
let startTime = now();
traces
| where timestamp > ago(24h)
| where message startswith "Conversation completed"
| extend ConversationId = tostring(customDimensions["Conversation.Id"])
| where isnotempty(ConversationId)
| summarize Count = count()
| extend QueryDuration = now() - startTime
| project Count, QueryDuration
```

## Expected Results (When Access Works)

- **Connectivity:** Should connect without authentication errors
- **Schema:** Should find traces table with timestamp, message, customDimensions columns
- **Discovery Query:** Should return conversation IDs (if any exist in last 24h)
- **Regional Filter:** Should show distribution by region, confirming filtering works
- **Performance:** Should execute in <5 seconds for 24h window

## Actions Required to Complete Validation

1. **Verify Azure subscription context** - may need different subscription
2. **Ensure RBAC permissions on Kusto cluster** - need Kusto Database User or Viewer role
3. **Confirm cluster URL and database name** - validate naming and location
4. **Check network access** - verify no firewall or private endpoint restrictions
5. **Validate authentication scope** - ensure token includes correct Kusto resource

## Recommended Next Steps

1. **Contact Sherlock team** to verify cluster access requirements
2. **Request access** to sherlockdiag-tel-int cluster in eastus region
3. **Confirm database existence** - validate SherlockTelemetry database is accessible
4. **Test with proper authentication** - ensure correctly scoped access token
5. **Re-run validation tests** after access is established

## Tools and Capabilities Available

- ✅ Azure PowerShell with Kusto module (Az.Kusto 2.4.0)
- ✅ Azure CLI with Kusto extension (v0.5.0)
- ✅ Rema Kusto MCP integration framework
- ❌ Direct cluster access (blocked by permissions)

---

**Note:** This validation cannot be completed until proper access to the Kusto cluster is established. The framework and tools are ready for testing once permissions are resolved.