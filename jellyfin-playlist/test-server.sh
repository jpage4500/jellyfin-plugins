dotnet build --configuration Release

docker stop jellyfin-dev
docker rm jellyfin-dev

docker run -d \
  --name jellyfin-dev \
  -p 8096:8096 \
  -v ~/jellyfin-test/config:/config \
  -v ~/jellyfin-test/Music:/media/music \
  -v ./bin/Release/net10.0:/config/plugins/FavoritesExporter \
  jellyfin/jellyfin:unstable