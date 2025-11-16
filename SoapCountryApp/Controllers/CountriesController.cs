using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using SoapCountryApp.Services;

namespace SoapCountryApp.Controllers
{
    public class CountriesController : Controller
    {
        private readonly SoapClientService _soap;

        public CountriesController(SoapClientService soap)
        {
            _soap = soap;
        }

        public async Task<IActionResult> Index()
        {
            var list = await _soap.GetCountriesAsync();
            return View(list); // view will expect List<Country>
        }

        public async Task<IActionResult> Continents()
        {
            var list = await _soap.GetContinentsAsync();
            return View(list); // view expecting List<(string Code,string Name)> or a viewmodel
        }
    }
}