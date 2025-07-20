FROM ruby:3.3-slim

RUN apt-get update && \
    apt-get install -y --no-install-recommends build-essential bash && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /srv/jekyll

COPY docs-site/Gemfile docs-site/Gemfile
RUN cd docs-site && bundle install

COPY docs/ docs/
COPY docs-site/ docs-site/
COPY README.md ROADMAP.md ./

RUN bash docs-site/scripts/prepare-docs.sh

WORKDIR /srv/jekyll/docs-site

EXPOSE 4000 35729

CMD ["bundle", "exec", "jekyll", "serve", "--host", "0.0.0.0", "--livereload", "--force_polling"]
