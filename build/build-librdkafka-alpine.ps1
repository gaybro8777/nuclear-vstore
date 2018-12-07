docker build -f .\Dockerfile.librdkafka-alpine --tag kafka-alpine-image .
$ContainerId = (docker run -d kafka-alpine-image sleep 10)
docker cp "$ContainerId/:/librdkafka/src/librdkafka.so.1" ./librdkafka.so
