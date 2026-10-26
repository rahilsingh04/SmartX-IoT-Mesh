FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.Api/SmartX.Api.csproj SmartX.Api/
RUN dotnet restore SmartX.Api/SmartX.Api.csproj
COPY SmartX.Api/ SmartX.Api/
WORKDIR /src/SmartX.Api
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 5080
ENV ASPNETCORE_URLS=http://+:5080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
