using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;

namespace SoapCountryApp.Services
{
    public class Country
    {
        public string ISOCode { get; set; }
        public string Name { get; set; }
    }

    public class SoapClientService
    {
        private readonly HttpClient _http;

        // Base URL for the SOAP endpoint (from Beeceptor doc)
        private const string BaseUrl = "https://soap-test-server.mock.beeceptor.com/CountryInfoService.wso";

        public SoapClientService(HttpClient http)
        {
            _http = http;
        }

        // Calls the ListOfCountryNamesByName operation (no input parameters)
        public async Task<List<Country>> GetCountriesAsync()
        {
            // SOAP envelope for the operation (uses the sample namespace used by the mock server)
            var soapBody = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                  <soap:Body>
                    <m:ListOfCountryNamesByName xmlns:m=""http://www.oorsprong.org/websamples.countryinfo"" />
                  </soap:Body>
                </soap:Envelope>";

            var content = new StringContent(soapBody, Encoding.UTF8, "application/xml");

            using var resp = await _http.PostAsync(BaseUrl, content);
            resp.EnsureSuccessStatusCode();
            var respString = await resp.Content.ReadAsStringAsync();

            // Load XML and parse with XDocument / LINQ-to-XML
            var doc = XDocument.Parse(respString);
            // the response uses: xmlns:m="http://www.oorsprong.org/websamples.countryinfo"
            XNamespace m = "http://www.oorsprong.org/websamples.countryinfo";
            var countries = doc
                .Descendants(m + "tCountryCodeAndName")
                .Select(x => new Country
                {
                    ISOCode = (string)x.Element(m + "sISOCode"),
                    Name = (string)x.Element(m + "sName")
                })
                .ToList();

            return countries;
        }

        // Example: ListOfContinentsByName (if you want continents)
        public async Task<List<(string Code, string Name)>> GetContinentsAsync()
        {
            var soapBody = @"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                  <soap:Body>
                    <m:ListOfContinentsByName xmlns:m=""http://www.oorsprong.org/websamples.countryinfo"" />
                  </soap:Body>
                </soap:Envelope>";

            var content = new StringContent(soapBody, Encoding.UTF8, "application/xml");
            using var resp = await _http.PostAsync(BaseUrl, content);
            resp.EnsureSuccessStatusCode();
            var respString = await resp.Content.ReadAsStringAsync();

            var doc = XDocument.Parse(respString);
            XNamespace m = "http://www.oorsprong.org/websamples.countryinfo";

            var continents = doc
                .Descendants(m + "tContinent")
                .Select(x => (
                    Code: (string)x.Element(m + "sCode"),
                    Name: (string)x.Element(m + "sName")
                ))
                .ToList();

            return continents;
        }
    }
}
