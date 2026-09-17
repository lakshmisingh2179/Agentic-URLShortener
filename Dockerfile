FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Shortener.slnx && dotnet build Shortener.slnx -c Release --no-restore
RUN dotnet run --project tests/Shortener.Tests -c Release --no-build -- --integration
RUN dotnet publish src/Shortener.Api -c Release --no-restore -o /app
FROM build AS engineering
ENV DOTNET_HOST_PATH=/usr/share/dotnet/dotnet AGENT_MODE=offline
ENTRYPOINT ["dotnet", "tools/EngineeringDemo/bin/Release/net10.0/EngineeringDemo.dll", "/src", "/work", "/evidence"]
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 DATA_DIR=/data AGENT_MODE=offline
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shortener.Api.dll"]
