---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Get-MgxTelemetry.md
schema: 2.0.0
---

# Get-MgxTelemetry

## SYNOPSIS
Return accumulated session telemetry from the resilience pipeline.

## SYNTAX

```
Get-MgxTelemetry [-Reset] [<CommonParameters>]
```

## DESCRIPTION
Get-MgxTelemetry returns accumulated telemetry from the current session's resilience pipeline. Reports request counts, retry/throttle breakdown, and timing per category so you can determine whether the bottleneck is throttling, retry backoff, rate-limiter queuing, or network latency.

Adaptive pacing surfaces here too: AdaptivePacingWaitMs / AdaptivePacingActivations count proactive waits, LastThrottlePercentage is the most recent x-ms-throttle-limit-percentage Graph reported (-1 = never seen), and PacingState is a per-workload line showing active rate caps, slow start, throttle proximity, and current latency against the session baseline. -Reset clears the counters but deliberately not the pacer's learned rate caps - those describe the tenant's current throttle regime, not accumulated statistics.

## EXAMPLES

### Example 1: View session telemetry
```powershell
Get-MgxTelemetry
```

Shows accumulated request counts, retries, throttle events, and timing.

### Example 2: Reset telemetry counters
```powershell
Get-MgxTelemetry -Reset
```

Returns current telemetry and resets all counters to zero.

### Example 3: Check throttle rate
```powershell
$t = Get-MgxTelemetry
"Throttle rate: $([math]::Round($t.ThrottleCount / $t.TotalRequests * 100, 1))%"
```

Calculates the percentage of requests that were throttled.

### Example 4: Watch adaptive pacing during a fan-out
```powershell
Get-MgxTelemetry | Select-Object AdaptivePacingWaitMs, AdaptivePacingActivations, LastThrottlePercentage, PacingState
```

Shows how much proactive pacing has happened and the current per-workload pacing state.

## PARAMETERS

### -Reset
Return current telemetry and reset all counters to zero.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject
Telemetry summary with request counts, retry/throttle breakdown, and timing.

## NOTES

## RELATED LINKS
[Set-MgxOption](Set-MgxOption.md)
[Get-MgxOption](Get-MgxOption.md)
