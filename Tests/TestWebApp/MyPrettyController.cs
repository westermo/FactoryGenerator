using FactoryGenerator.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace TestWebApp;

[Inject, Self, Scoped]
public class MyPrettyController : Controller
{
    private readonly MySupportingClass m_support;

    public MyPrettyController(MySupportingClass support)
    {
        m_support = support;
        str = "haha";
    }

    private readonly string str;

    [HttpGet("/lol")]
    public string Lol()
    {
        return (m_support.count++).ToString();
    }
}

[Inject, Self, Scoped]
public class MySupportingClass
{
    public int count = 0;
}