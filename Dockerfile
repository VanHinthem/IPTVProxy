FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/IPTVProxy/IPTVProxy.csproj src/IPTVProxy/
RUN dotnet restore src/IPTVProxy/IPTVProxy.csproj
COPY src/IPTVProxy/ src/IPTVProxy/
RUN dotnet publish src/IPTVProxy/IPTVProxy.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DataDirectory=/data
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "IPTVProxy.dll"]
