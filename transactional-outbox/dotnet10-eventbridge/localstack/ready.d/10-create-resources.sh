#!/usr/bin/env bash
# Idempotent LocalStack ready hook: EventBridge bus -> rule -> SQS target.
# Safe to re-run; any failure exits non-zero so the init status reports it.
set -euo pipefail

region="ap-southeast-2"
account_id="000000000000"
bus_name="orders"
queue_name="order-placed"
rule_name="route-order-placed"

if ! awslocal events describe-event-bus --name "$bus_name" --region "$region" >/dev/null 2>&1; then
  awslocal events create-event-bus --name "$bus_name" --region "$region" >/dev/null
fi

if ! queue_url="$(awslocal sqs get-queue-url --queue-name "$queue_name" --region "$region" --query QueueUrl --output text 2>/dev/null)"; then
  queue_url="$(awslocal sqs create-queue --queue-name "$queue_name" --region "$region" --query QueueUrl --output text)"
fi

queue_arn="arn:aws:sqs:${region}:${account_id}:${queue_name}"
rule_arn="arn:aws:events:${region}:${account_id}:rule/${bus_name}/${rule_name}"

awslocal events put-rule --event-bus-name "$bus_name" --name "$rule_name" --region "$region" \
  --event-pattern '{"source":["sample.orders"],"detail-type":["OrderPlaced"]}' >/dev/null

policy="$(printf '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"events.amazonaws.com"},"Action":"sqs:SendMessage","Resource":"%s","Condition":{"ArnEquals":{"aws:SourceArn":"%s"}}}]}' "$queue_arn" "$rule_arn")"
# JSON form: the CLI shorthand parser cannot handle commas/quotes inside the policy document.
attributes="{\"Policy\":\"${policy//\"/\\\"}\"}"
awslocal sqs set-queue-attributes --queue-url "$queue_url" --attributes "$attributes" --region "$region"

awslocal events put-targets --event-bus-name "$bus_name" --rule "$rule_name" --region "$region" \
  --targets "Id=order-placed-queue,Arn=$queue_arn" >/dev/null

echo "LocalStack resources ready: bus=$bus_name queue=$queue_name rule=$rule_name"
