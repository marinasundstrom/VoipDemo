FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build

WORKDIR /src

COPY src/VoiceApp/VoiceApp.csproj src/VoiceApp/
COPY src/Voip.Telephony/Voip.Telephony.csproj src/Voip.Telephony/
RUN dotnet restore src/VoiceApp/VoiceApp.csproj

COPY src/VoiceApp/ src/VoiceApp/
COPY src/Voip.Telephony/ src/Voip.Telephony/
RUN dotnet publish src/VoiceApp/VoiceApp.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "VoiceApp.dll"]