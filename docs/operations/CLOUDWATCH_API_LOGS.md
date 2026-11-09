# API error logs in CloudWatch

The EC2 Compose API service sends its container output to the CloudWatch Logs group `/hutube/ec2/api` in `ap-southeast-1`. API error responses are logged with their `TraceId`, status code, error code, and HTTP method. The error summary does not include request bodies, credentials, or user profile data.

## EC2 role permissions

The EC2 instance profile role must allow the Docker `awslogs` driver to create the log group and stream and upload events. Grant these CloudWatch Logs actions, scoped to the `/hutube/ec2/api` log group where supported:

- `logs:CreateLogGroup`
- `logs:CreateLogStream`
- `logs:PutLogEvents`

The Compose driver creates the group on first startup. Set a retention period for the group in CloudWatch Logs, for example 30 days, to control storage and cost.

## Find an error by trace ID

In AWS Console, open **CloudWatch → Logs → Log groups → `/hutube/ec2/api` → Logs Insights**. Run:

```text
fields @timestamp, @message
| filter @message like /PASTE_TRACE_ID_HERE/
| sort @timestamp desc
| limit 100
```

The log group receives new events after the API container is recreated with this Compose configuration. It does not backfill older errors.
