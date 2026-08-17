def greet(name):
    return f"hello {name}"


class Greeter:
    def greet(self, name):
        return greet(name)
