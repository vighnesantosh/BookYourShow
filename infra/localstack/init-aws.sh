#!/bin/bash
# Creates the messaging topology in LocalStack (mirrors what AWS CDK creates in the cloud):
#   one SNS topic -> one SQS queue per consumer, filtered on the "type" message attribute,
#   raw message delivery, and a DLQ (maxReceiveCount 5) behind every queue.
set -euo pipefail
REGION=us-east-1
TOPIC_ARN=$(awslocal sns create-topic --name bys-events --region $REGION --query TopicArn --output text)

make_queue() {   # name
  local name=$1
  local dlq_url dlq_arn url arn
  dlq_url=$(awslocal sqs create-queue --queue-name "$name-dlq" --region $REGION --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  url=$(awslocal sqs create-queue --queue-name "$name" --region $REGION \
        --attributes "{\"VisibilityTimeout\":\"30\",\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"$dlq_arn\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\"}" \
        --query QueueUrl --output text)
  awslocal sqs get-queue-attributes --queue-url "$url" --attribute-names QueueArn --query Attributes.QueueArn --output text
}

subscribe() {    # queueName eventType...
  local name=$1; shift
  local arn types sub
  arn=$(make_queue "$name")
  types=$(printf '"%s",' "$@"); types="[${types%,}]"
  sub=$(awslocal sns subscribe --topic-arn "$TOPIC_ARN" --protocol sqs --notification-endpoint "$arn" --region $REGION --query SubscriptionArn --output text)
  awslocal sns set-subscription-attributes --subscription-arn "$sub" --attribute-name RawMessageDelivery --attribute-value true
  awslocal sns set-subscription-attributes --subscription-arn "$sub" --attribute-name FilterPolicy --attribute-value "{\"type\":$types}"
  echo "queue $name <- $*"
}

subscribe inventory-events ShowPublished BookingConfirmed BookingFailed BookingExpired BookingCancelled
subscribe booking-events   PaymentSucceeded PaymentFailed HoldExpired
subscribe payment-events   BookingFailed BookingExpired BookingCancelled
make_queue inventory-hold-expiry > /dev/null   # direct delayed sends, no SNS subscription
echo "BookYourShow messaging topology ready"
