#!/bin/bash
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# A whole harness round on one AWS spot instance: started for the round, terminated when it ends.
#
#   spot-round.sh setup                       once per account and region (and after changing it)
#   spot-round.sh run <fix ref> <base ref> [out dir]
#   spot-round.sh status | down               what is running; terminate it
#
# Why: a round is a short burst of work (all eleven harnesses, fix and base, about 35 thread-hours
# the first time against a base) and nothing between rounds. One 64-core spot instance runs every
# container of the round at once, the fix and the base on the same machine, for about a dollar a
# round; the harness host (six cores) takes 90 minutes or more for the fix side alone. A round
# still takes as long as its longest piece, the working day (about 40 minutes at its pace).
#
# How: `setup` makes, in AWS_REGION (or the CLI's default), a key pair from SPOT_KEY (default
# ~/.ssh/id_ed25519.pub), a security group, an EBS volume (SPOT_VOLUME_GB, default 200) that holds
# Docker's data (the harness image, the aow-cache volume: the base results, the builds, NuGet) from
# one round to the next, and a launch template. `run` asks for one instance of SPOT_TYPES
# (price-capacity-optimized, in the volume's zone), opens SSH to this machine's address only,
# attaches the volume, points the Docker context "spot" at it, builds the harness image when
# Dockerfile or run-harness.sh changed, runs run-round.sh there with nothing sent to GitHub, and
# terminates the instance when it ends, fails or is stopped. The instance also shuts itself down
# (and so terminates) SPOT_MAX_HOURS (default 4) after it starts, whatever happens here.
# A spot instance can be taken back with two minutes' notice: the round's containers are then gone
# (marked !!) and the round is run again.
#
# The AWS CLI must be configured (`aws configure`) with a user allowed: ec2 Describe*, CreateFleet,
# RunInstances, CreateTags, TerminateInstances, CreateLaunchTemplate, DeleteLaunchTemplate,
# CreateSecurityGroup, AuthorizeSecurityGroupIngress, RevokeSecurityGroupIngress, ImportKeyPair,
# CreateVolume, AttachVolume, ssm:GetParameters (the Ubuntu image), and iam:CreateServiceLinkedRole
# (EC2 Fleet's role, the first time).
set -euo pipefail
[ $# -ge 1 ] || { sed -n 5,33p "$0"; exit 2; }
cmd=$1; shift
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
name=aow-harness-spot
types="${SPOT_TYPES:-c7a.16xlarge c7i.16xlarge c6a.16xlarge c6i.16xlarge m7a.16xlarge}"
max_hours="${SPOT_MAX_HOURS:-4}"
ctx=spot
command -v aws >/dev/null || { echo "!! the AWS CLI is not installed (brew install awscli; aws configure)"; exit 2; }
region="${AWS_REGION:-$(aws configure get region || true)}"
[ -n "$region" ] || { echo "!! no region: set AWS_REGION or run aws configure"; exit 2; }
export AWS_REGION="$region" AWS_PAGER=""
ec2() { aws ec2 "$@"; }

volume_id() {
  ec2 describe-volumes --filters "Name=tag:Name,Values=$name-cache" \
    --query 'Volumes[0].VolumeId' --output text | sed 's/^None$//'
}
running_ids() {
  ec2 describe-instances --filters "Name=tag:Name,Values=$name" \
    "Name=instance-state-name,Values=pending,running,stopping" \
    --query 'Reservations[].Instances[].InstanceId' --output text
}
sg_id() {
  ec2 describe-security-groups --filters "Name=group-name,Values=$name" \
    --query 'SecurityGroups[0].GroupId' --output text | sed 's/^None$//'
}

# The instance's first boot: the volume mounted at /data, holding Docker's data root and containerd's
# store (Ubuntu 24.04's Docker keeps images and containers in containerd's store, /var/lib/containerd,
# not in the data root: left on the 20 GB system disk it filled it and the first round waited for
# disk, 28 Nov 2026), Docker from Ubuntu,
# SSH open to the many connections a round makes at once (each docker command over the context is
# one), and the shutdown that ends it whatever happens on this side.
user_data() { # volume id
  local serial=${1/-/}
  cat <<EOF
#!/bin/bash
set -eux
shutdown -h +$(( max_hours * 60 ))
dev=/dev/disk/by-id/nvme-Amazon_Elastic_Block_Store_$serial
for i in \$(seq 1 300); do [ -e \$dev ] && break; sleep 2; done
blkid \$dev || mkfs.ext4 -q -L aowdata \$dev
mkdir -p /data && mount \$dev /data
# A volume laid out before containerd's store was on it has Docker records of images whose data was
# on a system disk long gone (a build then fails: "content digest ... not found"): start it afresh.
[ -e /data/.layout-2 ] || { rm -rf /data/docker /data/containerd; touch /data/.layout-2; }
mkdir -p /etc/docker && echo '{"data-root": "/data/docker"}' > /etc/docker/daemon.json
mkdir -p /data/containerd && ln -sfn /data/containerd /var/lib/containerd
apt-get update -q && apt-get install -y -q docker.io
usermod -aG docker ubuntu
printf 'MaxStartups 200:30:400\nMaxSessions 200\n' > /etc/ssh/sshd_config.d/aow-harness.conf
systemctl reload ssh
touch /run/aow-ready
EOF
}

case "$cmd" in
setup)
  key="${SPOT_KEY:-$HOME/.ssh/id_ed25519.pub}"
  [ -f "$key" ] || { echo "!! no public key $key (SPOT_KEY)"; exit 2; }
  if ! ec2 describe-key-pairs --key-names "$name" >/dev/null 2>&1; then
    ec2 import-key-pair --key-name "$name" --public-key-material "fileb://$key" >/dev/null
    echo "key pair $name from $key"
  fi
  vpc=$(ec2 describe-vpcs --filters Name=is-default,Values=true --query 'Vpcs[0].VpcId' --output text)
  [ "$vpc" != None ] || { echo "!! no default VPC in $region"; exit 2; }
  sg=$(sg_id)
  if [ -z "$sg" ]; then
    sg=$(ec2 create-security-group --group-name "$name" --vpc-id "$vpc" \
      --description "AgOpenWeb harness rounds: SSH from the machine running the round" --query GroupId --output text)
    echo "security group $sg"
  fi
  vol=$(volume_id)
  if [ -z "$vol" ]; then
    az="${SPOT_AZ:-$(ec2 describe-availability-zones --query 'AvailabilityZones[?State==`available`]|[0].ZoneName' --output text)}"
    vol=$(ec2 create-volume --availability-zone "$az" --size "${SPOT_VOLUME_GB:-200}" --volume-type gp3 \
      --tag-specifications "ResourceType=volume,Tags=[{Key=Name,Value=$name-cache}]" --query VolumeId --output text)
    echo "volume $vol in $az (Docker's data, kept between rounds)"
  fi
  ec2 delete-launch-template --launch-template-name "$name" >/dev/null 2>&1 || true
  ud=$(user_data "$vol" | base64 | tr -d '\n')
  ec2 create-launch-template --launch-template-name "$name" --launch-template-data "$(cat <<EOF
{
  "ImageId": "resolve:ssm:/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id",
  "KeyName": "$name",
  "SecurityGroupIds": ["$sg"],
  "InstanceInitiatedShutdownBehavior": "terminate",
  "BlockDeviceMappings": [{"DeviceName": "/dev/sda1", "Ebs": {"VolumeSize": 20, "VolumeType": "gp3", "DeleteOnTermination": true}}],
  "TagSpecifications": [{"ResourceType": "instance", "Tags": [{"Key": "Name", "Value": "$name"}]}],
  "UserData": "$ud"
}
EOF
)" >/dev/null
  echo "launch template $name in $region: ready (spot-round.sh run <fix> <base>)"
  ;;

status)
  ids=$(running_ids)
  if [ -z "$ids" ]; then echo "no instance"; else
    ec2 describe-instances --instance-ids $ids --query \
      'Reservations[].Instances[].[InstanceId,InstanceType,State.Name,Placement.AvailabilityZone,PublicIpAddress,LaunchTime]' --output text
  fi
  ;;

down)
  ids=$(running_ids)
  [ -z "$ids" ] || { ec2 terminate-instances --instance-ids $ids >/dev/null; echo "terminating $ids"; }
  ;;

run)
  [ $# -ge 2 ] || { sed -n 8p "$0"; exit 2; }
  fix=$1; base=$2; out="${3:-./harness-out/spot-$(date +%m%d-%H%M)}"
  [ -z "$(running_ids)" ] || { echo "!! an instance is already running (spot-round.sh status / down)"; exit 2; }
  vol=$(volume_id); sg=$(sg_id)
  [ -n "$vol" ] && [ -n "$sg" ] || { echo "!! run spot-round.sh setup first"; exit 2; }
  az=$(ec2 describe-volumes --volume-ids "$vol" --query 'Volumes[0].AvailabilityZone' --output text)
  subnet=$(ec2 describe-subnets --filters "Name=availability-zone,Values=$az" Name=default-for-az,Values=true \
    --query 'Subnets[0].SubnetId' --output text)

  # SSH from this machine's address only, as it is now.
  me=$(curl -fsS https://checkip.amazonaws.com | tr -d '[:space:]')
  old=$(ec2 describe-security-groups --group-ids "$sg" --query 'SecurityGroups[0].IpPermissions[].IpRanges[].CidrIp' --output text)
  for c in $old; do [ "$c" = "$me/32" ] || ec2 revoke-security-group-ingress --group-id "$sg" --protocol tcp --port 22 --cidr "$c" >/dev/null; done
  [[ " $old " == *" $me/32 "* ]] || ec2 authorize-security-group-ingress --group-id "$sg" --protocol tcp --port 22 --cidr "$me/32" >/dev/null

  overrides=""
  for t in $types; do overrides="$overrides{\"InstanceType\": \"$t\", \"SubnetId\": \"$subnet\"},"; done
  fleet=$(ec2 create-fleet --type instant --cli-input-json "$(cat <<EOF
{
  "Type": "instant",
  "TargetCapacitySpecification": {"TotalTargetCapacity": 1, "DefaultTargetCapacityType": "spot"},
  "SpotOptions": {"AllocationStrategy": "price-capacity-optimized"},
  "LaunchTemplateConfigs": [{
    "LaunchTemplateSpecification": {"LaunchTemplateName": "$name", "Version": "\$Latest"},
    "Overrides": [${overrides%,}]
  }]
}
EOF
)")
  id=$(echo "$fleet" | jq -r '.Instances[0].InstanceIds[0] // empty')
  [ -n "$id" ] || { echo "!! no spot instance:"; echo "$fleet" | jq -r '.Errors[]? | "   \(.LaunchTemplateAndOverrides.Overrides.InstanceType): \(.ErrorMessage)"'; exit 1; }

  ip=""
  teardown() {
    trap - EXIT INT TERM
    echo "=== terminating $id $(date +%H:%M)"
    ec2 terminate-instances --instance-ids "$id" >/dev/null || echo "!! terminate $id by hand (spot-round.sh down)"
    docker context rm -f "$ctx" >/dev/null 2>&1 || true
    [ -z "$ip" ] || ssh-keygen -R "$ip" >/dev/null 2>&1 || true
  }
  trap teardown EXIT
  trap 'exit 130' INT TERM

  ec2 wait instance-running --instance-ids "$id"
  type=$(ec2 describe-instances --instance-ids "$id" --query 'Reservations[0].Instances[0].InstanceType' --output text)
  ip=$(ec2 describe-instances --instance-ids "$id" --query 'Reservations[0].Instances[0].PublicIpAddress' --output text)
  echo "=== $id $type in $az at $ip $(date +%H:%M)"
  # The last round's instance lets go of the volume as it terminates, which can take a minute.
  ec2 wait volume-available --volume-ids "$vol"
  ec2 attach-volume --volume-id "$vol" --instance-id "$id" --device /dev/sdf >/dev/null

  ssh_opts=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 -o BatchMode=yes)
  for i in $(seq 1 90); do
    ssh "${ssh_opts[@]}" "ubuntu@$ip" test -e /run/aow-ready 2>/dev/null && break
    [ "$i" -lt 90 ] || { echo "!! the instance was not ready after 15 minutes"; exit 1; }
    sleep 10
  done
  # Images and containers must be on the volume, not the system disk (see user_data).
  store=$(ssh "${ssh_opts[@]}" "ubuntu@$ip" 'findmnt -n -o TARGET -T "$(readlink -f /var/lib/containerd)"')
  [ "$store" = /data ] || { echo "!! containerd's store is on $store, not the volume: run spot-round.sh setup again"; exit 1; }
  docker context rm -f "$ctx" >/dev/null 2>&1 || true
  docker context create "$ctx" --docker "host=ssh://ubuntu@$ip" >/dev/null
  ncpu=$(docker --context "$ctx" info --format '{{.NCPU}}')
  echo "=== Docker ready, $ncpu threads $(date +%H:%M)"

  # The image, built there when its files changed: it stays on the volume between rounds.
  hash=$(cat "$here/Dockerfile" "$here/run-harness.sh" | shasum | cut -c1-16)
  have=$(docker --context "$ctx" image inspect -f '{{index .Config.Labels "aow.hash"}}' aow-harness:latest 2>/dev/null || true)
  if [ "$have" != "$hash" ]; then
    echo "=== building the harness image $(date +%H:%M)"
    docker --context "$ctx" build -q --label "aow.hash=$hash" -t aow-harness:latest "$here" >/dev/null
  fi

  echo "=== round $(date +%H:%M)"
  set +e
  RUNNER_CONTEXT="$ctx" GITHUB_PART="" "$here/run-round.sh" "$fix" "$base" "$out"
  rc=$?
  echo "=== round ended $(date +%H:%M), exit $rc"
  exit $rc
  ;;

*) sed -n 5,33p "$0"; exit 2 ;;
esac
