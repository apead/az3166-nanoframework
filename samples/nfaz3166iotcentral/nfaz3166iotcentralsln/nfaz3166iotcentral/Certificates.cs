namespace nfaz3166iotcentral
{
    /// <summary>
    /// Root certificates used to verify the TLS certificates of the Device Provisioning Service and Azure IoT Hub (PEM).
    /// Azure is moving its services to the Microsoft TLS roots, so the bundle trusts both the DigiCert root
    /// that has been used so far and the new Microsoft roots. Checked against the live Azure certificate
    /// chains with openssl (October 2026).
    /// </summary>
    internal static class Certificates
    {
        // Each root is a separate string: the app tries them one at a time (a single trusted certificate keeps the TLS
        // handshake light on the board's memory). PEM strings have to end with a null character.

        /// <summary>DigiCert Global Root G2: the root Azure's endpoints chain to today.</summary>
        public const string DigiCertGlobalRootG2 =
            "-----BEGIN CERTIFICATE-----\n" +
            "MIIDjjCCAnagAwIBAgIQAzrx5qcRqaC7KGSxHQn65TANBgkqhkiG9w0BAQsFADBh\n" +
            "MQswCQYDVQQGEwJVUzEVMBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3\n" +
            "d3cuZGlnaWNlcnQuY29tMSAwHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBH\n" +
            "MjAeFw0xMzA4MDExMjAwMDBaFw0zODAxMTUxMjAwMDBaMGExCzAJBgNVBAYTAlVT\n" +
            "MRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5j\n" +
            "b20xIDAeBgNVBAMTF0RpZ2lDZXJ0IEdsb2JhbCBSb290IEcyMIIBIjANBgkqhkiG\n" +
            "9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuzfNNNx7a8myaJCtSnX/RrohCgiN9RlUyfuI\n" +
            "2/Ou8jqJkTx65qsGGmvPrC3oXgkkRLpimn7Wo6h+4FR1IAWsULecYxpsMNzaHxmx\n" +
            "1x7e/dfgy5SDN67sH0NO3Xss0r0upS/kqbitOtSZpLYl6ZtrAGCSYP9PIUkY92eQ\n" +
            "q2EGnI/yuum06ZIya7XzV+hdG82MHauVBJVJ8zUtluNJbd134/tJS7SsVQepj5Wz\n" +
            "tCO7TG1F8PapspUwtP1MVYwnSlcUfIKdzXOS0xZKBgyMUNGPHgm+F6HmIcr9g+UQ\n" +
            "vIOlCsRnKPZzFBQ9RnbDhxSJITRNrw9FDKZJobq7nMWxM4MphQIDAQABo0IwQDAP\n" +
            "BgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNVHQ4EFgQUTiJUIBiV\n" +
            "5uNu5g/6+rkS7QYXjzkwDQYJKoZIhvcNAQELBQADggEBAGBnKJRvDkhj6zHd6mcY\n" +
            "1Yl9PMWLSn/pvtsrF9+wX3N3KjITOYFnQoQj8kVnNeyIv/iPsGEMNKSuIEyExtv4\n" +
            "NeF22d+mQrvHRAiGfzZ0JFrabA0UWTW98kndth/Jsw1HKj2ZL7tcu7XUIOGZX1NG\n" +
            "Fdtom/DzMNU+MeKNhJ7jitralj41E6Vf8PlwUHBHQRFXGU7Aj64GxJUTFy8bJZ91\n" +
            "8rGOmaFvE7FBcf6IKshPECBV1/MUReXgRPTqh5Uykw7+U0b6LJ3/iyK5S9kJRaTe\n" +
            "pLiaWN0bfVKfjllDiIGknibVb63dDcY3fe0Dkhvld1927jyNxF1WW6LZZm6zNTfl\n" +
            "MrY=\n" +
            "-----END CERTIFICATE-----\n" + "\0";

        /// <summary>Microsoft TLS RSA Root G2: Azure's new RSA root, used by services moving off DigiCert.</summary>
        public const string MicrosoftRsaRootG2 =
            "-----BEGIN CERTIFICATE-----\n" +
            "MIIFgDCCA2igAwIBAgIQZIbjsmkYD79AQDkuLlNLmzANBgkqhkiG9w0BAQwFADBR\n" +
            "MQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSIw\n" +
            "IAYDVQQDExlNaWNyb3NvZnQgVExTIFJTQSBSb290IEcyMB4XDTI1MDQxMDE4MzYz\n" +
            "MFoXDTQwMDQxMDE4NDM1MVowUTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jv\n" +
            "c29mdCBDb3Jwb3JhdGlvbjEiMCAGA1UEAxMZTWljcm9zb2Z0IFRMUyBSU0EgUm9v\n" +
            "dCBHMjCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAN/qi59H4SgQca9B\n" +
            "1hnbklfea6vmLt6tOmCDtxOYrFu5Mgt7659yJ2tdQKu+GAaTsg3SEu24dPV/L+qo\n" +
            "F5T2MxZXit4gfCHdt9qs3k5ksFrNS3Xz7wJkTe1S2qOPBOiR+Nf7Pxn9TkU5Xugp\n" +
            "w9byZ6+zAJTBjeHwShiYOfrACYQzQQUDBav0deFTpSGSXERM5mBXpJACl8fV2JME\n" +
            "MkrthVHt1sW6oD82ii9vTDmoBGkihDt1FPfZPrSp0AdBOIF+Rkgd6BnmcLigqhjd\n" +
            "cr9+fbVsThlwEYQEE4HUYv2ca/Ec3f/sp+Dmx40xS7/yazPM0Yy2/rEbo0qoXw5/\n" +
            "MXY4PwdHbAGorKUU2oKwXx9svIkawBIqy3uXHyguMRaKU0x1Zvl9AdbCYKpljzGe\n" +
            "csvMmy3lRBmTV6tujsLZLmWwci3nocOhfkkdWWfWCdQYEm3AlUV4yfFgauT27gYk\n" +
            "O3NKWhHCPpdhwHju7xOnMOJKOC3uWBcTILc2Kt8LMrTCOLGR3RKwRbZ8wLEdYqGd\n" +
            "3XESFZYBnQoJ9bHds9ngOEI09mm2w68Wc/7UQ/hD5Fj5T7Pz7NY9lDpAOBWrDIbQ\n" +
            "ZM4aY+iElFUJWrYEttE+mhXHZBGA0BuCDk3/RZ6yZQclH9uayMbs1luFUASfHMtd\n" +
            "IIJnnnni3+jy6G/EIHIwhuWddSc1AgMBAAGjVDBSMA4GA1UdDwEB/wQEAwIBhjAP\n" +
            "BgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTekYZIt6ExWTHxS18HqdyIedqodjAQ\n" +
            "BgkrBgEEAYI3FQEEAwIBADANBgkqhkiG9w0BAQwFAAOCAgEAdDOaUHmugVYlz5tv\n" +
            "URHeVRKQk2wz4C4kDF5kYjRONhm1oBTztcZ406PtsDsKXpI+chsuCoPev5+o0zmo\n" +
            "DtgjNQcFj7bzxhrUQyQXk5obQPB53cnAD3MaBYeLCrrbJIi0hL3N6n8b7XalNAe6\n" +
            "bg7qczpIwu3xnX/3jgUT28vTfShczLK+oHkQKibrcuSmoZcedsQ8flaa8+Y9z+LV\n" +
            "BBlqPNc45eO7HgUP3UgrfDmd6U9Nd6i9SSif57koWzb0cyCCVhbVwVpNxO37B8xo\n" +
            "N67DdFOrlygAA0aE0do5nk8QSj+ZQis1qDmVt36BPmyv/byprsCSTMI0JTsXp5uL\n" +
            "QAoj8orWuAA/Ul5Wu3nri4n4JIngzeYO+s8ISGbNypagtcsvb0QT5sY4nvEEkPET\n" +
            "SLVIQij9/4FIYlFVlJ9nSV9sSP7H0e2RoLQbttBruxNgoKifkv0zVcyBTzPjmkej\n" +
            "xTOZnDVvfB+kS189DzljD/otUI6sGZT9v5fZjljMgGlAZzi2xhfrHKqvhFZSn2Al\n" +
            "MnuQZvLjlzEiIsG1Oj2JFOG0kGq1MmKwljhVBvuKryqDTHrqGa3E9JQattAXUoet\n" +
            "+QzlJ5xIPcay1LE+MA+TM6wC5aCgUmmg8sVILr9GwyZF5urX+Usp4ta44Em3upSv\n" +
            "eM+HJyMso/L+NgtC+eHXJLur5sg=\n" +
            "-----END CERTIFICATE-----\n" + "\0";

        /// <summary>Microsoft TLS ECC Root G2: Azure's new ECC root.</summary>
        public const string MicrosoftEccRootG2 =
            "-----BEGIN CERTIFICATE-----\n" +
            "MIICMTCCAbegAwIBAgIQcuICK8WywbBNJQVuYuJ2eTAKBggqhkjOPQQDAzBRMQsw\n" +
            "CQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSIwIAYD\n" +
            "VQQDExlNaWNyb3NvZnQgVExTIEVDQyBSb290IEcyMB4XDTI1MDQxMDIwNTIzNloX\n" +
            "DTQwMDQxMDIwNTg1OVowUTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m\n" +
            "dCBDb3Jwb3JhdGlvbjEiMCAGA1UEAxMZTWljcm9zb2Z0IFRMUyBFQ0MgUm9vdCBH\n" +
            "MjB2MBAGByqGSM49AgEGBSuBBAAiA2IABFHCOIT2ay617TcWSuXj100AhDtAVPwJ\n" +
            "5vTFp6ZrVkROrWCeFZiOA+7iss11PVhkwBYM31rcLJ1AW75LVCrOjzy/lbyOrInv\n" +
            "aT4XBvd9wbgCZSPL9R0a41Oh9Ad+CotdWaNUMFIwDgYDVR0PAQH/BAQDAgGGMA8G\n" +
            "A1UdEwEB/wQFMAMBAf8wHQYDVR0OBBYEFG+rftr/l0Ny7Dtnd96CYTWIR0KFMBAG\n" +
            "CSsGAQQBgjcVAQQDAgEAMAoGCCqGSM49BAMDA2gAMGUCMQDYM1rR4cySss0f8PKM\n" +
            "sT2ytNTKmNzaiQX0G1dx35RNInYFUElNkCmSLL8vN+W5deICMAGSaVidU7LIVWZb\n" +
            "uslQWiqBto7PEdDyfoTcfzbfwy3aTfgisNizizSfnTEcrA8RGQ==\n" +
            "-----END CERTIFICATE-----\n" + "\0";
    }
}
