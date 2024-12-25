#!/bin/bash

# docker-push.sh - Automates Docker image building, tagging, and pushing to Docker Hub

# Ensure the script exits on error
set -e

# Check if the required arguments are provided
if [ "$#" -ne 4 ]; then
  echo "Usage: $0 <dockerfile_path> <local_image> <dockerhub_username> <version>"
  echo "Example: ./docker-push.sh . lebiru/fileservice myusername v1.1.0"
  exit 1
fi

# Assign arguments to variables
DOCKERFILE_PATH=$1
LOCAL_IMAGE=$2
DOCKERHUB_USERNAME=$3
VERSION=$4
REMOTE_IMAGE="${DOCKERHUB_USERNAME}/$(basename ${LOCAL_IMAGE})"

# Step 1: Build the Docker image
echo "Building Docker image: ${LOCAL_IMAGE} from path: ${DOCKERFILE_PATH}"
docker build -t "${LOCAL_IMAGE}" "${DOCKERFILE_PATH}"

# Step 2: Log in to Docker Hub (requires prior docker login)
echo "Ensuring Docker login..."
docker login

# Step 3: Tag the new version
echo "Tagging image: ${LOCAL_IMAGE} as ${REMOTE_IMAGE}:${VERSION}"
docker tag "${LOCAL_IMAGE}" "${REMOTE_IMAGE}:${VERSION}"

# Step 4: Push the tagged image
echo "Pushing image: ${REMOTE_IMAGE}:${VERSION}"
docker push "${REMOTE_IMAGE}:${VERSION}"

# Step 5: Optionally tag and push as 'latest'
echo "Tagging image as 'latest'"
docker tag "${LOCAL_IMAGE}" "${REMOTE_IMAGE}:latest"
echo "Pushing image: ${REMOTE_IMAGE}:latest"
docker push "${REMOTE_IMAGE}:latest"

# Success message
echo "Docker image built and pushed successfully!"
echo "Version: ${VERSION}, Image: ${REMOTE_IMAGE}"
