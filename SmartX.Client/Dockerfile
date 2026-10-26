FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.Client/SmartX.Client.csproj SmartX.Client/
RUN dotnet restore SmartX.Client/SmartX.Client.csproj
COPY SmartX.Client/ SmartX.Client/
WORKDIR /src/SmartX.Client
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 5090
ENV ASPNETCORE_URLS=http://+:5090
ENV SmartXApi__BaseUrl=http://smartx-api:5080
ENTRYPOINT ["dotnet", "SmartX.Client.dll"]
